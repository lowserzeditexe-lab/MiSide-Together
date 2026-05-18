using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx;
using BepInEx.Unity.IL2CPP.Utils;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.UI;
using MiSideCoop.UI;

namespace MiSideCoop.Update
{
    /// <summary>
    /// Vérifie au démarrage si une nouvelle release du mod existe sur GitHub.
    /// Si oui, propose une popup à l'utilisateur (Update / Skip / Not now).
    /// Si accepté, télécharge le zip, génère un script PowerShell de mise à jour
    /// qui attend la fermeture du jeu, remplace les fichiers, puis relance le jeu.
    ///
    /// Conception IL2CPP-safe :
    ///   • HttpClient (System.Net.Http) — code .NET 6 pur, immune au stripping.
    ///   • Parsing JSON manuel via Regex — pas de dépendance à Newtonsoft / System.Text.Json.
    ///   • Pas d'API Unity strippée (uGUI uniquement, clic manuel en Update()).
    ///   • Script PowerShell généré à la volée (Windows 10+ : Expand-Archive natif).
    /// </summary>
    public class AutoUpdater : MonoBehaviour
    {
        public static AutoUpdater Instance { get; private set; }

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        // ── État interne (rempli par le thread background) ───────────────────
        private volatile bool   _checkDone;
        private volatile bool   _updateAvailable;
        private volatile string _remoteVersion;
        private volatile string _downloadUrl;
        private volatile string _checkError;

        // ── État UI ───────────────────────────────────────────────────────────
        private GameObject _canvas;
        private GameObject _acceptBtn;
        private GameObject _skipBtn;
        private GameObject _laterBtn;
        private bool _promptShown;
        private bool _downloadInProgress;
        private Text _bodyText;

        // ── Style ─────────────────────────────────────────────────────────────
        private static readonly Color OverlayBg = new Color(0.05f, 0.05f, 0.08f, 0.85f);
        private static readonly Color PanelBg   = new Color(0.08f, 0.06f, 0.11f, 1f);
        private static readonly Color BorderClr = new Color(1f, 0.435f, 0.659f, 1f); // pink accent (#FF6FA8)
        private static readonly Color BtnBg     = new Color(0.16f, 0.12f, 0.22f, 1f);
        private static readonly Color BtnAccept = new Color(1f, 0.435f, 0.659f, 1f);
        private static readonly Color TextClr   = new Color(0.95f, 0.94f, 1f, 1f);

        // ─────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────────
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            // Décale le check de 2s pour laisser la scène / UI s'initialiser.
            MonoBehaviourExtensions.StartCoroutine(this, CheckRoutine());
        }

        private void Update()
        {
            // Affiche la popup une fois que le check background est terminé
            // et qu'une nouvelle version a été détectée.
            if (_checkDone && _updateAvailable && !_promptShown)
            {
                _promptShown = true;
                try { BuildPromptUI(); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        $"[Co-op] Failed to render update prompt: {ex.Message}");
                }
            }

            if (_canvas != null && !_downloadInProgress)
            {
                HandleButtonClicks();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Étape 1 — Check version sur GitHub
        // ─────────────────────────────────────────────────────────────────────
        [HideFromIl2Cpp]
        private IEnumerator CheckRoutine()
        {
            // Petit délai pour ne pas spammer la stack de boot
            float t = 0f;
            while (t < 2f) { t += Time.deltaTime; yield return null; }

            var repo = MiSideCoopPlugin.UpdateRepo?.Value;
            var enabled = MiSideCoopPlugin.UpdateCheckEnabled?.Value ?? true;
            if (!enabled || string.IsNullOrWhiteSpace(repo))
            {
                _checkDone = true;
                yield break;
            }

            MiSideCoopPlugin.Logger?.LogInfo($"[Co-op] Checking for updates ({repo})...");

            var url = $"https://api.github.com/repos/{repo}/releases/latest";
            bool done = false;
            string body = null;
            string err = null;

            new Thread(() =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    // GitHub API REQUIERT un User-Agent
                    req.Headers.Add("User-Agent", "MiSideTogether-AutoUpdater");
                    req.Headers.Add("Accept", "application/vnd.github+json");

                    var resp = _http.SendAsync(req).Result;
                    if (!resp.IsSuccessStatusCode)
                    {
                        err = $"HTTP {(int)resp.StatusCode}";
                        return;
                    }
                    body = resp.Content.ReadAsStringAsync().Result;
                }
                catch (Exception ex) { err = ex.Message; }
                finally { done = true; }
            }) { IsBackground = true }.Start();

            while (!done) yield return null;

            if (err != null || body == null)
            {
                _checkError = err ?? "no response";
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] Update check failed: {_checkError}. Skipping.");
                _checkDone = true;
                yield break;
            }

            // Parse minimal du JSON (regex)
            var tag = ExtractJsonString(body, "tag_name");
            var zipUrl = ExtractFirstZipAsset(body);
            if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(zipUrl))
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    "[Co-op] Update check: no usable release asset found on GitHub.");
                _checkDone = true;
                yield break;
            }

            // Compare versions
            if (!SemVer.TryParse(tag, out var remote) ||
                !SemVer.TryParse(PluginInfo.PLUGIN_VERSION, out var local))
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] Update check: failed to parse versions (remote='{tag}', local='{PluginInfo.PLUGIN_VERSION}').");
                _checkDone = true;
                yield break;
            }

            if (remote.CompareTo(local) <= 0)
            {
                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] Already up to date (local v{local}, remote v{remote}).");
                _checkDone = true;
                yield break;
            }

            // Skip explicite par l'utilisateur ?
            var skipped = MiSideCoopPlugin.UpdateSkipVersion?.Value;
            if (!string.IsNullOrEmpty(skipped) && skipped.Trim() == remote.ToString())
            {
                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] Update v{remote} skipped by user preference.");
                _checkDone = true;
                yield break;
            }

            _remoteVersion = remote.ToString();
            _downloadUrl   = zipUrl;
            _updateAvailable = true;
            _checkDone = true;
            MiSideCoopPlugin.Logger?.LogInfo(
                $"[Co-op] Update available: v{local} → v{remote}. Prompting user.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Étape 2 — Popup uGUI (canvas dédié, sort-order > CoopMenuUI)
        // ─────────────────────────────────────────────────────────────────────
        private void BuildPromptUI()
        {
            _canvas = new GameObject("MiSideCoopUpdatePrompt");
            _canvas.transform.SetParent(this.transform, false);
            var canvas = _canvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32100; // au-dessus du menu co-op (32000)
            var scaler = _canvas.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _canvas.AddComponent<GraphicRaycaster>();

            // Overlay sombre plein écran (bloque les clics derrière)
            var overlay = CreateImage(_canvas, "Overlay", OverlayBg);
            var orec = overlay.GetComponent<RectTransform>();
            orec.anchorMin = Vector2.zero; orec.anchorMax = Vector2.one;
            orec.offsetMin = Vector2.zero; orec.offsetMax = Vector2.zero;
            overlay.raycastTarget = true;

            // Panel central
            var panel = CreateImage(_canvas, "Panel", PanelBg);
            var prec = panel.GetComponent<RectTransform>();
            prec.anchorMin = new Vector2(0.5f, 0.5f);
            prec.anchorMax = new Vector2(0.5f, 0.5f);
            prec.pivot     = new Vector2(0.5f, 0.5f);
            prec.sizeDelta = new Vector2(600f, 320f);
            prec.anchoredPosition = Vector2.zero;

            // Bordure pink (image fine)
            var border = CreateImage(_canvas, "Border", BorderClr);
            var brec = border.GetComponent<RectTransform>();
            brec.anchorMin = new Vector2(0.5f, 0.5f);
            brec.anchorMax = new Vector2(0.5f, 0.5f);
            brec.pivot     = new Vector2(0.5f, 0.5f);
            brec.sizeDelta = new Vector2(604f, 6f);
            brec.anchoredPosition = new Vector2(0f, 157f);

            // Titre
            CreateText(panel.gameObject, "Title",
                $"UPDATE AVAILABLE  ·  v{_remoteVersion}",
                24, FontStyle.Bold, TextAnchor.UpperCenter,
                new Vector2(0f, -28f), new Vector2(560f, 40f));

            // Body
            _bodyText = CreateText(panel.gameObject, "Body",
                $"A newer version of MiSide Together is available on GitHub.\n" +
                $"Current: v{PluginInfo.PLUGIN_VERSION}    →    Latest: v{_remoteVersion}\n\n" +
                $"Click UPDATE NOW to download and install automatically.\n" +
                $"The game will restart.",
                15, FontStyle.Normal, TextAnchor.UpperCenter,
                new Vector2(0f, -82f), new Vector2(540f, 140f));

            // Boutons (en bas)
            _acceptBtn = CreateButton(panel.gameObject, "BtnAccept", "UPDATE NOW",
                new Vector2(-180f, -130f), new Vector2(170f, 44f), BtnAccept, Color.white);

            _skipBtn = CreateButton(panel.gameObject, "BtnSkip", $"SKIP v{_remoteVersion}",
                new Vector2(0f, -130f), new Vector2(170f, 44f), BtnBg, TextClr);

            _laterBtn = CreateButton(panel.gameObject, "BtnLater", "NOT NOW",
                new Vector2(180f, -130f), new Vector2(170f, 44f), BtnBg, TextClr);
        }

        private void HandleButtonClicks()
        {
            if (!Input.GetMouseButtonDown(0)) return;
            var mp = (Vector2)Input.mousePosition;

            if (_acceptBtn != null && IsInRect(_acceptBtn, mp))
            {
                _downloadInProgress = true;
                if (_bodyText != null)
                    _bodyText.text = "Downloading update...\nPlease wait, the game will restart automatically.";
                MonoBehaviourExtensions.StartCoroutine(this, DownloadAndApplyRoutine());
                return;
            }
            if (_skipBtn != null && IsInRect(_skipBtn, mp))
            {
                if (MiSideCoopPlugin.UpdateSkipVersion != null)
                    MiSideCoopPlugin.UpdateSkipVersion.Value = _remoteVersion;
                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] User skipped update v{_remoteVersion} permanently.");
                Destroy(_canvas);
                _canvas = null;
                return;
            }
            if (_laterBtn != null && IsInRect(_laterBtn, mp))
            {
                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] User postponed update v{_remoteVersion} (will check again next launch).");
                Destroy(_canvas);
                _canvas = null;
                return;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Étape 3 — Download + script PowerShell + Application.Quit
        // ─────────────────────────────────────────────────────────────────────
        [HideFromIl2Cpp]
        private IEnumerator DownloadAndApplyRoutine()
        {
            // 1) Télécharge le zip dans BepInEx/cache
            var cacheDir = Path.Combine(Paths.BepInExRootPath, "cache");
            try { Directory.CreateDirectory(cacheDir); } catch { }
            var zipPath = Path.Combine(cacheDir, $"MiSideCoop_update_v{_remoteVersion}.zip");

            bool done = false;
            string err = null;
            new Thread(() =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, _downloadUrl);
                    req.Headers.Add("User-Agent", "MiSideTogether-AutoUpdater");
                    var resp = _http.SendAsync(req).Result;
                    if (!resp.IsSuccessStatusCode) { err = $"HTTP {(int)resp.StatusCode}"; return; }
                    var bytes = resp.Content.ReadAsByteArrayAsync().Result;
                    File.WriteAllBytes(zipPath, bytes);
                }
                catch (Exception ex) { err = ex.Message; }
                finally { done = true; }
            }) { IsBackground = true }.Start();

            while (!done) yield return null;

            if (err != null)
            {
                MiSideCoopPlugin.Logger?.LogWarning($"[Co-op] Update download failed: {err}");
                if (_bodyText != null)
                    _bodyText.text = $"Download failed: {err}\nThe game will continue running.\nClose this prompt.";
                _downloadInProgress = false;
                yield break;
            }

            // 2) Détermine les chemins du jeu via des helpers ISOLÉS.
            //    En IL2CPP MiSide, Process.GetCurrentProcess().MainModule peut
            //    être strippé et le JIT échoue AVANT le try/catch englobant
            //    (cf. fix v1.2.x sur SetPixels32). Chaque appel risqué doit
            //    donc être dans sa propre méthode pour que la défaillance JIT
            //    reste confinée et soit récupérable.
            var gameRoot = ResolveGameRoot();          // priorité : Application.dataPath
            var gameExe  = ResolveGameExePath(gameRoot); // priorité : scan *.exe
            int pid      = TryGetCurrentPid();

            if (string.IsNullOrEmpty(gameRoot) || string.IsNullOrEmpty(gameExe))
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    "[Co-op] Could not resolve game paths. Opening download page instead.");
                OpenReleasePageFallback();
                yield break;
            }

            string scriptPath = Path.Combine(cacheDir, "miside_coop_update.ps1");
            bool scriptLaunched = false;
            try
            {
                File.WriteAllText(scriptPath, BuildPowerShellScript(), Encoding.UTF8);
                scriptLaunched = TryLaunchUpdater(scriptPath, zipPath, gameRoot, gameExe, pid);
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] Update script write failed: {ex.Message}");
            }

            if (!scriptLaunched)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    "[Co-op] Updater script could not start. Opening download page instead.");
                OpenReleasePageFallback();
                yield break;
            }

            // 3) Quitte proprement le jeu pour que le script puisse remplacer les fichiers
            MiSideCoopPlugin.Logger?.LogInfo(
                "[Co-op] Update staged. Quitting game so updater can replace files...");
            yield return null;
            try { Application.Quit(); } catch { /* will be force-killed by script */ }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers isolés pour confiner les éventuels JIT-fails IL2CPP
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Racine du jeu via <c>Application.dataPath</c>.
        /// Application.dataPath = "&lt;GameRoot&gt;/MiSideFull_Data" → parent = GameRoot.
        /// Cette API Unity est universellement présente et non strippée.
        /// </summary>
        private static string ResolveGameRoot()
        {
            try
            {
                var data = Application.dataPath;
                if (string.IsNullOrEmpty(data)) return null;
                return Path.GetDirectoryName(data);
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning($"[Co-op] ResolveGameRoot: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Trouve l'exécutable du jeu en scannant la racine pour le 1er *.exe
        /// non-Unity (exclut UnityCrashHandler64.exe). Évite Process.MainModule
        /// qui peut être strippé en IL2CPP.
        /// </summary>
        private static string ResolveGameExePath(string gameRoot)
        {
            if (string.IsNullOrEmpty(gameRoot)) return null;
            try
            {
                var exes = Directory.GetFiles(gameRoot, "*.exe", SearchOption.TopDirectoryOnly);
                foreach (var p in exes)
                {
                    var name = Path.GetFileNameWithoutExtension(p);
                    if (name.IndexOf("UnityCrashHandler", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    if (name.IndexOf("CrashReport", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    return p;
                }
                return exes.Length > 0 ? exes[0] : null;
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning($"[Co-op] ResolveGameExePath: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// PID du processus courant. Isolé car Process.GetCurrentProcess()
        /// touche du natif et peut JIT-fail en IL2CPP.
        /// </summary>
        private static int TryGetCurrentPid()
        {
            try { return Process.GetCurrentProcess().Id; }
            catch { return 0; }
        }

        /// <summary>
        /// Lance le script PowerShell. Isolé car ProcessStartInfo + Process.Start
        /// peuvent être strippés en IL2CPP.
        /// </summary>
        private static bool TryLaunchUpdater(string scriptPath, string zipPath,
                                             string gameRoot, string gameExe, int pid)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\" " +
                                $"-GamePid {pid} " +
                                $"-ZipPath \"{zipPath}\" " +
                                $"-GameRoot \"{gameRoot}\" " +
                                $"-GameExe \"{gameExe}\"",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = gameRoot
                };
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning($"[Co-op] TryLaunchUpdater: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Plan B : ouvre la page de release dans le navigateur. L'utilisateur
        /// télécharge et installe manuellement. Aucune dépendance native.
        /// </summary>
        private void OpenReleasePageFallback()
        {
            try
            {
                var repo = MiSideCoopPlugin.UpdateRepo?.Value ?? "lowserzeditexe-lab/MiSide-Together";
                var url  = $"https://github.com/{repo}/releases/tag/v{_remoteVersion}";
                Application.OpenURL(url);
                if (_bodyText != null)
                    _bodyText.text =
                        $"Auto-install unavailable on this build.\n" +
                        $"Opening download page in your browser:\n{url}";
            }
            catch (Exception ex)
            {
                if (_bodyText != null)
                    _bodyText.text =
                        $"Could not open browser ({ex.Message}).\n" +
                        $"Visit https://github.com/{MiSideCoopPlugin.UpdateRepo?.Value} manually.";
            }
            _downloadInProgress = false;
        }

        private static string BuildPowerShellScript() => @"
param(
    [int]   $GamePid,
    [string]$ZipPath,
    [string]$GameRoot,
    [string]$GameExe
)
$ErrorActionPreference = 'SilentlyContinue'

# 1) Attendre que le jeu se ferme (max 60s)
$timeout = 60
while (Get-Process -Id $GamePid -ErrorAction SilentlyContinue) {
    Start-Sleep -Milliseconds 500
    $timeout -= 0.5
    if ($timeout -le 0) { break }
}

# 2) Petit délai pour libérer les locks Windows
Start-Sleep -Milliseconds 1500

# 3) v1.3.4 — Nettoyage des doublons DLL hérités des templates v1.0+.
#    Le template release contenait par erreur BOTH :
#      BepInEx/plugins/MiSideCoop.dll               <-- canonique
#      BepInEx/plugins/MiSideCoop/MiSideCoop.dll    <-- duplicate dossier
#    Résultat : BepInEx loggait 'Skipping [MiSide Together 1.3.x] because a
#    newer version exists' et ne chargeait pas le plugin proprement au
#    redémarrage. On supprime le doublon dossier AVANT extraction pour
#    que le nouvel arbre soit propre.
$pluginsDir = Join-Path $GameRoot 'BepInEx\plugins'
$staleDir   = Join-Path $pluginsDir 'MiSideCoop'
if (Test-Path $staleDir) {
    try { Remove-Item -Recurse -Force $staleDir -ErrorAction SilentlyContinue } catch { }
}

# 4) Décompresse le zip directement à la racine du jeu (-Force = overwrite)
try {
    Expand-Archive -Path $ZipPath -DestinationPath $GameRoot -Force
} catch {
    [System.Windows.Forms.MessageBox]::Show(
        ""MiSide Together update failed to extract:`n$($_.Exception.Message)`n`nYou can extract '$ZipPath' manually."",
        ""MiSide Together"") | Out-Null
    exit 1
}

# 5) v1.3.4 — Si le zip réintroduisait par erreur le sous-dossier doublon,
#    on le re-supprime APRÈS extraction (idempotent, safe).
if (Test-Path $staleDir) {
    try { Remove-Item -Recurse -Force $staleDir -ErrorAction SilentlyContinue } catch { }
}

# 6) Supprime le zip temporaire
Remove-Item -Force $ZipPath -ErrorAction SilentlyContinue

# 7) Relance le jeu
Start-Process -FilePath $GameExe -WorkingDirectory $GameRoot
";

        // ─────────────────────────────────────────────────────────────────────
        // Helpers JSON (regex-only)
        // ─────────────────────────────────────────────────────────────────────
        private static string ExtractJsonString(string json, string key)
        {
            // "key": "value"  — tolère espaces et échappements basiques
            var rx = new Regex("\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            var m = rx.Match(json);
            if (!m.Success) return null;
            return Regex.Unescape(m.Groups[1].Value);
        }

        private static string ExtractFirstZipAsset(string json)
        {
            // Cherche un browser_download_url qui finit par .zip
            var rx = new Regex("\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip)\"");
            var m = rx.Match(json);
            return m.Success ? m.Groups[1].Value : null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers UI
        // ─────────────────────────────────────────────────────────────────────
        private static Image CreateImage(GameObject parent, string name, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<RectTransform>();
            var img = go.AddComponent<Image>();
            img.color = color;
            return img;
        }

        private static Text CreateText(GameObject parent, string name, string content,
            int size, FontStyle style, TextAnchor align,
            Vector2 anchoredPos, Vector2 sizeDelta)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = sizeDelta;

            var t = go.AddComponent<Text>();
            t.text = content;
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = align;
            t.color = TextClr;
            t.font  = CoopMenuUI.SharedFont ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow   = VerticalWrapMode.Overflow;
            return t;
        }

        private static GameObject CreateButton(GameObject parent, string name, string label,
            Vector2 anchoredPos, Vector2 size, Color bg, Color textColor)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            var img = go.AddComponent<Image>();
            img.color = bg;
            img.raycastTarget = true;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            var lrt = labelGo.AddComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            var t = labelGo.AddComponent<Text>();
            t.text = label;
            t.fontSize = 14;
            t.fontStyle = FontStyle.Bold;
            t.alignment = TextAnchor.MiddleCenter;
            t.color = textColor;
            t.font = CoopMenuUI.SharedFont ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

            return go;
        }

        private static bool IsInRect(GameObject btn, Vector2 screenPos)
        {
            var rt = btn.GetComponent<RectTransform>();
            if (rt == null) return false;
            return RectTransformUtility.RectangleContainsScreenPoint(rt, screenPos, null);
        }
    }
}

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
        private GameObject _okBtn; // v1.6.5 — bouton OK du popup post-download
        private bool _promptShown;
        private bool _downloadInProgress;
        private bool _restartPromptShown; // v1.6.5
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
            // v1.5.7 — Triage du flow : auto-install vs toast non-bloquant vs popup modale (legacy).
            if (_checkDone && _updateAvailable && !_promptShown)
            {
                _promptShown = true;

                // Mode AUTO-INSTALL (par défaut) : on télécharge + applique
                // immédiatement, sans afficher d'UI. Le jeu se relance avec
                // la nouvelle version via la cascade v1.5.5 (run_bepinex.bat).
                if (MiSideCoopPlugin.UpdateAutoInstall?.Value == true)
                {
                    MiSideCoopPlugin.Logger?.LogInfo(
                        $"[Co-op] AutoInstall enabled — installing update v{_remoteVersion} silently. "
                      + "Game will restart automatically.");
                    _downloadInProgress = true;
                    MonoBehaviourExtensions.StartCoroutine(this, DownloadAndApplyRoutine());
                    // On affiche quand même un mini-toast "Installing update..." pour
                    // que l'utilisateur sache pourquoi le jeu va redémarrer.
                    try { BuildInstallingToastUI(); }
                    catch (Exception ex)
                    {
                        MiSideCoopPlugin.Logger?.LogWarning(
                            $"[Co-op] BuildInstallingToastUI failed: {ex.Message}");
                    }
                    return;
                }

                // Sinon : toast non-bloquant en bas à droite.
                try { BuildToastUI(); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        $"[Co-op] Failed to render update toast: {ex.Message}");
                }
            }

            if (_canvas != null && !_downloadInProgress)
            {
                HandleButtonClicks();
            }

            // v1.6.5 — Popup OK : on attend que l'utilisateur clique pour quitter.
            if (_restartPromptShown && _canvas != null && _okBtn != null)
            {
                if (Input.GetMouseButtonDown(0) && IsInRect(_okBtn, (Vector2)Input.mousePosition))
                {
                    MiSideCoopPlugin.Logger?.LogInfo("[Co-op] User clicked OK on restart prompt. Quitting.");
                    try { Application.Quit(); } catch { }
                }
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
        // v1.5.7 — TOAST UI bottom-right (non-bloquant)
        // ─────────────────────────────────────────────────────────────────────
        //
        // Remplace l'ancienne BuildPromptUI() plein écran. Affiche un petit
        // panel 360x100 ancré bottom-right (anchor 1,0) avec :
        //   • Titre : "Update available — vX.Y.Z"
        //   • Body : "Click INSTALL to apply and restart"
        //   • Boutons : [INSTALL] [✕]
        //   • raycastTarget UNIQUEMENT sur le panel (l'overlay est invisible
        //     et ne bloque pas les clics du gameplay derrière).
        //
        private void BuildToastUI()
        {
            _canvas = new GameObject("MiSideCoopUpdateToast");
            _canvas.transform.SetParent(this.transform, false);
            var canvas = _canvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32100;
            var scaler = _canvas.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _canvas.AddComponent<GraphicRaycaster>();

            // Pas d'overlay sombre — le toast n'occupe que le coin bas-droite
            // et n'intercepte AUCUN clic en dehors de sa propre surface.

            // Panel
            var panel = CreateImage(_canvas, "ToastPanel", PanelBg);
            var prec = panel.GetComponent<RectTransform>();
            prec.anchorMin = new Vector2(1f, 0f);
            prec.anchorMax = new Vector2(1f, 0f);
            prec.pivot     = new Vector2(1f, 0f);
            prec.sizeDelta = new Vector2(380f, 110f);
            prec.anchoredPosition = new Vector2(-24f, 24f); // 24px margin bottom-right
            panel.raycastTarget = true; // intercepte les clics SUR le panel uniquement

            // Border pink à gauche (effet "notif")
            var border = CreateImage(_canvas, "ToastBorder", BorderClr);
            var brec = border.GetComponent<RectTransform>();
            brec.SetParent(panel.transform, false);
            brec.anchorMin = new Vector2(0f, 0f);
            brec.anchorMax = new Vector2(0f, 1f);
            brec.pivot     = new Vector2(0f, 0.5f);
            brec.sizeDelta = new Vector2(4f, 0f);
            brec.anchoredPosition = Vector2.zero;
            border.raycastTarget = false;

            // Titre
            var title = CreateText(panel.gameObject, "ToastTitle",
                $"UPDATE AVAILABLE · v{_remoteVersion}",
                14, FontStyle.Bold, TextAnchor.UpperLeft,
                new Vector2(8f, -8f), new Vector2(280f, 22f));
            title.color = BorderClr;

            // Body
            _bodyText = CreateText(panel.gameObject, "ToastBody",
                $"Current v{PluginInfo.PLUGIN_VERSION} → v{_remoteVersion}\nClick INSTALL to apply and restart.",
                12, FontStyle.Normal, TextAnchor.UpperLeft,
                new Vector2(8f, -32f), new Vector2(280f, 50f));

            // Bouton INSTALL (large, accent)
            _acceptBtn = CreateButton(panel.gameObject, "ToastBtnInstall", "INSTALL",
                new Vector2(-105f, 16f), new Vector2(150f, 30f), BtnAccept, Color.white);
            var arect = _acceptBtn.GetComponent<RectTransform>();
            arect.anchorMin = new Vector2(1f, 0f);
            arect.anchorMax = new Vector2(1f, 0f);
            arect.pivot     = new Vector2(1f, 0f);
            arect.anchoredPosition = new Vector2(-58f, 16f);

            // Bouton CLOSE (×) — pour fermer le toast sans installer
            _laterBtn = CreateButton(panel.gameObject, "ToastBtnClose", "✕",
                new Vector2(-8f, 16f), new Vector2(40f, 30f), BtnBg, TextClr);
            var lrect = _laterBtn.GetComponent<RectTransform>();
            lrect.anchorMin = new Vector2(1f, 0f);
            lrect.anchorMax = new Vector2(1f, 0f);
            lrect.pivot     = new Vector2(1f, 0f);
            lrect.anchoredPosition = new Vector2(-12f, 16f);

            _skipBtn = null; // pas de bouton SKIP en mode toast (présent dans le menu config)
        }

        /// <summary>
        /// v1.5.7 — Mini-toast non-cliquable affiché quand AutoInstall est ON,
        /// pour informer l'utilisateur que la maj se télécharge avant le restart.
        /// </summary>
        private void BuildInstallingToastUI()
        {
            _canvas = new GameObject("MiSideCoopUpdateInstalling");
            _canvas.transform.SetParent(this.transform, false);
            var canvas = _canvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32100;
            var scaler = _canvas.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var panel = CreateImage(_canvas, "InstallingPanel", PanelBg);
            var prec = panel.GetComponent<RectTransform>();
            prec.anchorMin = new Vector2(1f, 0f);
            prec.anchorMax = new Vector2(1f, 0f);
            prec.pivot     = new Vector2(1f, 0f);
            prec.sizeDelta = new Vector2(360f, 64f);
            prec.anchoredPosition = new Vector2(-24f, 24f);
            panel.raycastTarget = false; // bloque RIEN

            var border = CreateImage(_canvas, "InstallingBorder", BorderClr);
            var brec = border.GetComponent<RectTransform>();
            brec.SetParent(panel.transform, false);
            brec.anchorMin = new Vector2(0f, 0f);
            brec.anchorMax = new Vector2(0f, 1f);
            brec.pivot     = new Vector2(0f, 0.5f);
            brec.sizeDelta = new Vector2(4f, 0f);
            brec.anchoredPosition = Vector2.zero;
            border.raycastTarget = false;

            var title = CreateText(panel.gameObject, "InstallingTitle",
                $"INSTALLING v{_remoteVersion}…",
                14, FontStyle.Bold, TextAnchor.UpperLeft,
                new Vector2(12f, -10f), new Vector2(330f, 22f));
            title.color = BorderClr;

            _bodyText = CreateText(panel.gameObject, "InstallingBody",
                "Downloading update. A popup will ask you to restart when done.",
                11, FontStyle.Normal, TextAnchor.UpperLeft,
                new Vector2(12f, -32f), new Vector2(330f, 26f));
        }

        /// <summary>
        /// v1.6.5 — Popup modale plein écran affichée APRÈS le téléchargement.
        /// Annonce que la mise à jour est prête, et qu'il faut relancer le jeu
        /// manuellement. Un bouton OK ferme le jeu. La relance auto a été
        /// retirée pour fiabilité (cf. bug v1.5.x/v1.6.x : BepInEx non injecté
        /// quand le jeu était relancé via Start-Process direct).
        /// </summary>
        private void BuildRestartPromptUI()
        {
            _canvas = new GameObject("MiSideCoopUpdateRestart");
            _canvas.transform.SetParent(this.transform, false);
            var canvas = _canvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32200; // au-dessus du toast
            var scaler = _canvas.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _canvas.AddComponent<GraphicRaycaster>();

            // Overlay sombre plein écran (bloque les clics et l'attention)
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
            prec.sizeDelta = new Vector2(620f, 280f);
            prec.anchoredPosition = Vector2.zero;
            panel.raycastTarget = true;

            // Bordure pink (ligne supérieure)
            var border = CreateImage(_canvas, "Border", BorderClr);
            var brec = border.GetComponent<RectTransform>();
            brec.anchorMin = new Vector2(0.5f, 0.5f);
            brec.anchorMax = new Vector2(0.5f, 0.5f);
            brec.pivot     = new Vector2(0.5f, 0.5f);
            brec.sizeDelta = new Vector2(624f, 6f);
            brec.anchoredPosition = new Vector2(0f, 137f);

            // Titre
            CreateText(panel.gameObject, "RestartTitle",
                $"MISE À JOUR PRÊTE  ·  v{_remoteVersion}",
                22, FontStyle.Bold, TextAnchor.UpperCenter,
                new Vector2(0f, -24f), new Vector2(580f, 36f));

            // Body
            _bodyText = CreateText(panel.gameObject, "RestartBody",
                "La nouvelle version a été téléchargée.\n\n" +
                "Cliquez OK pour fermer le jeu.\n" +
                "Relancez ensuite MiSide manuellement — la mise à jour\n" +
                "sera appliquée à la fermeture, et BepInEx s'injectera\n" +
                "correctement au prochain démarrage.",
                15, FontStyle.Normal, TextAnchor.UpperCenter,
                new Vector2(0f, -68f), new Vector2(560f, 140f));

            // Bouton OK (centré, accent)
            _okBtn = CreateButton(panel.gameObject, "BtnOK", "OK — FERMER LE JEU",
                new Vector2(0f, -218f), new Vector2(260f, 48f), BtnAccept, Color.white);

            // Reset des autres boutons pour que HandleButtonClicks ne s'y trompe pas.
            _acceptBtn = null;
            _skipBtn   = null;
            _laterBtn  = null;
            _restartPromptShown = true;
        }


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
        // Étape 3 — Download + script PowerShell + Popup OK
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
            var gameRoot = ResolveGameRoot();
            var gameExe  = ResolveGameExePath(gameRoot);
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

            // 3) v1.6.5 — POPUP "OK pour fermer". Plus de relance auto.
            // Le user clique OK → Application.Quit → script PS extrait → Windows
            // MessageBox demande à l'user de relancer manuellement.
            MiSideCoopPlugin.Logger?.LogInfo(
                "[Co-op] Update downloaded. Showing OK-to-quit popup (user must relaunch manually).");
            try
            {
                if (_canvas != null) { Destroy(_canvas); _canvas = null; }
                BuildRestartPromptUI();
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] BuildRestartPromptUI failed: {ex.Message}. Quitting directly.");
                try { Application.Quit(); } catch { }
            }
            _downloadInProgress = false;
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
                int steamAppId = 2527500;
                try { steamAppId = MiSideCoopPlugin.UpdateSteamAppId?.Value ?? 2527500; } catch { }

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\" " +
                                $"-GamePid {pid} " +
                                $"-ZipPath \"{zipPath}\" " +
                                $"-GameRoot \"{gameRoot}\" " +
                                $"-GameExe \"{gameExe}\" " +
                                $"-SteamAppId {steamAppId}",
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
    [string]$GameExe,
    [int]   $SteamAppId = 2527500
)
$ErrorActionPreference = 'SilentlyContinue'

# v1.6.5 — UPDATE SCRIPT SIMPLIFIÉ
#
# Plus de cascade de relance (Steam URL, run_bepinex.bat, doorstop env vars).
# La relance auto causait régulièrement des bugs (BepInEx non injecté →
# jeu vanilla, ou Start-Process avec DLL search order incorrect).
#
# Nouveau flow :
#   1) Attendre que le jeu se ferme (l'user a cliqué OK dans la popup in-game)
#   2) Nettoyer les anciens fichiers de plugin
#   3) Décompresser le zip à la racine du jeu
#   4) Afficher une Windows MessageBox demandant à l'user de relancer manuellement
#   5) Exit
#
# Avantage : 100% fiable. La relance manuelle par l'utilisateur garantit
# l'injection BepInEx correcte via la méthode habituelle (Steam launch,
# raccourci bureau, run_bepinex.bat, etc.).

# 1) Attendre que le jeu se ferme (max 60s)
$timeout = 60
while (Get-Process -Id $GamePid -ErrorAction SilentlyContinue) {
    Start-Sleep -Milliseconds 500
    $timeout -= 0.5
    if ($timeout -le 0) { break }
}

# Petit délai pour libérer les locks Windows.
Start-Sleep -Milliseconds 1500

# 2) Nettoyage des doublons DLL hérités.
$pluginsDir = Join-Path $GameRoot 'BepInEx\plugins'
$staleDir   = Join-Path $pluginsDir 'MiSideCoop'
if (Test-Path $staleDir) {
    try { Remove-Item -Recurse -Force $staleDir -ErrorAction SilentlyContinue } catch { }
}
if (Test-Path $pluginsDir) {
    try {
        Get-ChildItem -Path $pluginsDir -Filter 'MiSideCoop*' -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne 'MiSideCoop.dll' -and -not $_.PSIsContainer } |
            ForEach-Object { Remove-Item -Force $_.FullName -ErrorAction SilentlyContinue }
    } catch { }
}

# 3) Décompresse le zip à la racine du jeu (-Force = overwrite).
$extractOk = $false
try {
    Expand-Archive -Path $ZipPath -DestinationPath $GameRoot -Force
    $extractOk = $true
} catch {
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
        [System.Windows.Forms.MessageBox]::Show(
            ""MiSide Together : echec de l'extraction de la mise a jour.`n`n$($_.Exception.Message)`n`nTu peux extraire manuellement le fichier :`n$ZipPath`nvers le dossier du jeu :`n$GameRoot"",
            ""MiSide Together — Erreur d'extraction"") | Out-Null
    } catch { }
    exit 1
}

# 3b) Défense en profondeur : applatir les wrappers MiSideCoop_vX.Y.Z/.
try {
    $wrappers = Get-ChildItem -Path $GameRoot -Directory -Filter 'MiSideCoop_v*' -ErrorAction SilentlyContinue
    foreach ($w in $wrappers) {
        $inner = $w.FullName
        try {
            Get-ChildItem -Path $inner -Force -ErrorAction SilentlyContinue | ForEach-Object {
                $dest = Join-Path $GameRoot $_.Name
                if ($_.PSIsContainer) {
                    Copy-Item -Path $_.FullName -Destination $dest -Recurse -Force -ErrorAction SilentlyContinue
                } else {
                    Copy-Item -Path $_.FullName -Destination $dest -Force -ErrorAction SilentlyContinue
                }
            }
            Remove-Item -Path $inner -Recurse -Force -ErrorAction SilentlyContinue
        } catch { }
    }
} catch { }

# Re-nettoyer le sous-dossier doublon (idempotent).
if (Test-Path $staleDir) {
    try { Remove-Item -Recurse -Force $staleDir -ErrorAction SilentlyContinue } catch { }
}

# 4) Supprime le zip temporaire.
Remove-Item -Force $ZipPath -ErrorAction SilentlyContinue

# 5) Windows MessageBox : informe l'user que la mise a jour est faite et
#    qu'il doit relancer le jeu manuellement.
try {
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
    [System.Windows.Forms.MessageBox]::Show(
        ""MiSide Together a ete mis a jour avec succes.`n`nRelance MiSide manuellement (Steam, raccourci, ou run_bepinex.bat).`nBepInEx sera correctement injecte au prochain demarrage."",
        ""MiSide Together — Mise a jour terminee"") | Out-Null
} catch { }

# Log debug.
try {
    $logFile = Join-Path $GameRoot 'BepInEx\LogOutput.txt'
    $logDir  = Split-Path $logFile -Parent
    if (Test-Path $logDir) {
        Add-Content -Path $logFile -Value ""[MiSideCoop AutoUpdater] Update extracted. User must relaunch manually."" -ErrorAction SilentlyContinue
    }
} catch { }
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

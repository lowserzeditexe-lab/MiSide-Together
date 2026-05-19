using System;
using System.Collections;
using System.Net.Http;
using System.Text;
using System.Threading;
using BepInEx.Unity.IL2CPP.Utils;   // MonoBehaviourExtensions.StartCoroutine
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace MiSideCoop.Relay
{
    /// <summary>
    /// Gère la génération des codes de room à 6 caractères et
    /// la communication avec le serveur de relais Node.js.
    ///
    /// ⚠️  Historique stripping IL2CPP MiSide (v1.0 → v1.1.7) :
    ///   • Animator.parameters        → strippé (v1.1.4)
    ///   • MonoBehaviour.StartCoroutine(IEnumerator) → strippé (v1.1.4)
    ///   • new UnityWebRequest(string,string) → strippé (v1.1.4)
    ///   • UnityWebRequest.Put         → strippé (v1.1.5)
    ///   • UploadHandlerRaw..ctor      → strippé (v1.1.6)
    ///   • DownloadHandlerBuffer..ctor → strippé (v1.1.7)  ← appelé EN INTERNE par UnityWebRequest.Get/Delete
    ///
    /// Conclusion : l'API UnityWebRequest est INUTILISABLE dans MiSide.
    /// Solution v1.1.8 : on bascule sur System.Net.Http.HttpClient — code .NET 6
    /// pur, immune au stripping IL2CPP. Les requêtes sont lancées sur un
    /// thread background, la coroutine attend (`yield return null`) le flag
    /// de complétion avant d'invoquer le callback sur le thread principal.
    ///
    /// Endpoints utilisés :
    ///   GET /register/:code/:port    → { success, ip, port }   (alias GET, v1.1.7+)
    ///   GET /resolve/:code           → { ip, port }
    ///   GET /room/:code/delete       → { success }             (alias GET, v1.1.8+)
    /// </summary>
    public class RoomManager : MonoBehaviour
    {

        public static RoomManager Instance { get; private set; }

        // Charset sans caractères ambigus (O/0, I/1)
        private const string Charset = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        // HttpClient partagé (recommandation .NET : un seul par application).
        // Timeout par défaut configuré par requête via CancellationTokenSource.
        private static readonly HttpClient _http = new HttpClient();

        private void Awake()
        {
            if (Instance != null) { Destroy(this); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ── Génération locale du code ─────────────────────────────────────────

        public string GenerateRoomCode()
        {
            var rng = new System.Random();
            var sb  = new StringBuilder(6);
            for (int i = 0; i < 6; i++)
                sb.Append(Charset[rng.Next(Charset.Length)]);
            return sb.ToString();
        }

        // ── API publique (callbacks) ──────────────────────────────────────────

        [HideFromIl2Cpp]
        public void RegisterRoom(string code, System.Action<bool> callback)
            => MonoBehaviourExtensions.StartCoroutine(this, RegisterCoroutine(code, callback));

        [HideFromIl2Cpp]
        public void ResolveRoom(string code, System.Action<string, int, string> callback)
            => MonoBehaviourExtensions.StartCoroutine(this, ResolveCoroutine(code, callback));

        [HideFromIl2Cpp]
        public void UnregisterRoom(string code)
            => MonoBehaviourExtensions.StartCoroutine(this, UnregisterCoroutine(code));

        // ── Helper HTTP : exécute un GET sur thread background ────────────────
        //
        // Retourne via callback (sur le main thread Unity) : (success, body, error).
        // Le `yield return null` dans la coroutine appelante attend que le flag
        // `done` passe à true. Aucune dépendance Unity/IL2CPP — full managed .NET.
        private static IEnumerator HttpGetAsync(string url, int timeoutSec,
                                                System.Action<bool, string, string> onComplete)
        {
            bool   done    = false;
            bool   ok      = false;
            string body    = null;
            string error   = null;

            var thread = new Thread(() =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                    var resp = _http.GetAsync(url, cts.Token).GetAwaiter().GetResult();
                    body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (resp.IsSuccessStatusCode)
                    {
                        ok = true;
                    }
                    else
                    {
                        error = $"HTTP {(int)resp.StatusCode}";
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                finally
                {
                    done = true;
                }
            }) { IsBackground = true };
            thread.Start();

            while (!done) yield return null;

            onComplete?.Invoke(ok, body, error);
        }

        // ── Coroutines HTTP (IL2CPP-safe via HttpClient) ──────────────────────

        [HideFromIl2Cpp]
        private IEnumerator RegisterCoroutine(string code, System.Action<bool> callback)
        {
            string relay = MiSideCoopPlugin.RelayServerUrl.Value;
            int    port  = MiSideCoopPlugin.NetworkPort.Value;
            string url   = $"{relay}/register/{code.ToUpper()}/{port}";

            yield return HttpGetAsync(url, 10, (ok, body, error) =>
            {
                if (!ok)
                    MiSideCoopPlugin.Logger.LogWarning(
                        $"[Relay] Registration failed ({error}). Direct-IP fallback active.");
                else
                    MiSideCoopPlugin.Logger.LogInfo($"[Relay] Room '{code}' registered.");

                callback?.Invoke(ok);
            });
        }

        [HideFromIl2Cpp]
        private IEnumerator ResolveCoroutine(string code, System.Action<string, int, string> callback)
        {
            string relay = MiSideCoopPlugin.RelayServerUrl.Value;
            string url   = $"{relay}/resolve/{code.ToUpper()}";

            yield return HttpGetAsync(url, 10, (ok, body, error) =>
            {
                if (!ok)
                {
                    string err = (error != null && error.Contains("404"))
                        ? $"Code '{code}' not found. Check the code and ensure the host is online."
                        : $"Network error: {error}";
                    callback?.Invoke(null, 0, err);
                    return;
                }

                try
                {
                    string ip      = ExtractJson(body, "ip");
                    string portStr = ExtractJson(body, "port");
                    int    port    = int.TryParse(portStr, out int p)
                                        ? p
                                        : MiSideCoopPlugin.NetworkPort.Value;

                    MiSideCoopPlugin.Logger.LogInfo($"[Relay] Code '{code}' resolved → {ip}:{port}");
                    callback?.Invoke(ip, port, null);
                }
                catch (Exception ex)
                {
                    callback?.Invoke(null, 0, $"Response parse error: {ex.Message}");
                }
            });
        }

        [HideFromIl2Cpp]
        private IEnumerator UnregisterCoroutine(string code)
        {
            string relay = MiSideCoopPlugin.RelayServerUrl.Value;
            // Endpoint GET alias /room/:code/delete (v1.1.8+) — on évite DELETE
            // car même si UnityWebRequest.Delete a une factory propre, son
            // implémentation interne dépend probablement aussi de
            // DownloadHandlerBuffer (cf. crash v1.1.7 sur Get). HttpClient
            // pourrait faire DELETE directement, mais un alias GET côté relais
            // garde l'API HTTP simple et symétrique.
            string url = $"{relay}/room/{code.ToUpper()}/delete";

            yield return HttpGetAsync(url, 5, (ok, body, error) =>
            {
                if (ok)
                    MiSideCoopPlugin.Logger.LogInfo($"[Relay] Room '{code}' deleted from relay.");
                else
                    MiSideCoopPlugin.Logger.LogWarning(
                        $"[Relay] Room '{code}' deletion failed: {error}");
            });
        }

        // ── Parsing JSON minimal (sans dépendance externe) ────────────────────
        private static string ExtractJson(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return string.Empty;

            string search = $"\"{key}\":";
            int idx = json.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return string.Empty;

            idx += search.Length;
            // Saute les espaces et guillemets d'ouverture
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == '"')) idx++;

            int end = idx;
            while (end < json.Length && json[end] != '"' && json[end] != ',' && json[end] != '}')
                end++;

            return json.Substring(idx, end - idx).Trim('"', ' ');
        }
    }
}

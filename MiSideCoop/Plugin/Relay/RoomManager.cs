using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace MiSideCoop.Relay
{
    /// <summary>
    /// Gère la génération des codes de room à 6 caractères et
    /// la communication avec le serveur de relais Node.js.
    ///
    /// Endpoints utilisés :
    ///   POST   /register        { code, port }   → { success, ip, port }
    ///   GET    /resolve/:code                    → { ip, port }
    ///   DELETE /room/:code                       → { success }
    /// </summary>
    public class RoomManager : MonoBehaviour
    {
        public RoomManager(IntPtr ptr) : base(ptr) { }

        public static RoomManager Instance { get; private set; }

        // Charset sans caractères ambigus (O/0, I/1)
        private const string Charset = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        private void Awake()
        {
            if (Instance != null) { Destroy(this); return; }
            Instance = this;
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

        public void RegisterRoom(string code, System.Action<bool> callback)
            => StartCoroutine(RegisterCoroutine(code, callback));

        public void ResolveRoom(string code, System.Action<string, int, string> callback)
            => StartCoroutine(ResolveCoroutine(code, callback));

        public void UnregisterRoom(string code)
            => StartCoroutine(UnregisterCoroutine(code));

        // ── Coroutines HTTP ───────────────────────────────────────────────────

        private IEnumerator RegisterCoroutine(string code, System.Action<bool> callback)
        {
            string relay = MiSideCoopPlugin.RelayServerUrl.Value;
            int    port  = MiSideCoopPlugin.NetworkPort.Value;

            string json   = $"{{\"code\":\"{code}\",\"port\":{port}}}";
            byte[] body   = Encoding.UTF8.GetBytes(json);

            using var req = new UnityWebRequest($"{relay}/register", "POST")
            {
                uploadHandler   = new UploadHandlerRaw(body),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout         = 10
            };
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            bool ok = req.result == UnityWebRequest.Result.Success;
            if (!ok)
                MiSideCoopPlugin.Logger.LogWarning(
                    $"[Relay] Enregistrement échoué ({req.error}). Mode connexion directe activé.");
            else
                MiSideCoopPlugin.Logger.LogInfo($"[Relay] Room '{code}' enregistrée.");

            callback?.Invoke(ok);
        }

        private IEnumerator ResolveCoroutine(string code, System.Action<string, int, string> callback)
        {
            string relay = MiSideCoopPlugin.RelayServerUrl.Value;

            using var req = UnityWebRequest.Get($"{relay}/resolve/{code.ToUpper()}");
            req.timeout = 10;

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                string err = req.responseCode == 404
                    ? $"Code '{code}' introuvable. Vérifiez le code et que l'hôte est connecté."
                    : $"Erreur réseau : {req.error}";
                callback?.Invoke(null, 0, err);
                yield break;
            }

            try
            {
                string ip   = ExtractJson(req.downloadHandler.text, "ip");
                string portStr = ExtractJson(req.downloadHandler.text, "port");
                int    port = int.TryParse(portStr, out int p) ? p : MiSideCoopPlugin.NetworkPort.Value;

                MiSideCoopPlugin.Logger.LogInfo($"[Relay] Code '{code}' résolu → {ip}:{port}");
                callback?.Invoke(ip, port, null);
            }
            catch (Exception ex)
            {
                callback?.Invoke(null, 0, $"Erreur parsing réponse : {ex.Message}");
            }
        }

        private IEnumerator UnregisterCoroutine(string code)
        {
            string relay = MiSideCoopPlugin.RelayServerUrl.Value;
            using var req = UnityWebRequest.Delete($"{relay}/room/{code}");
            req.timeout = 5;
            yield return req.SendWebRequest();
            MiSideCoopPlugin.Logger.LogInfo($"[Relay] Room '{code}' supprimée du relais.");
        }

        // ── Parsing JSON minimal (sans dépendance externe, IL2CPP safe) ───────
        private static string ExtractJson(string json, string key)
        {
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

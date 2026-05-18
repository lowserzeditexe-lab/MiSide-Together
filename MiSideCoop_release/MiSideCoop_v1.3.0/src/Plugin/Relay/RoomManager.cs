using System.Text;
using UnityEngine;

namespace MiSideCoop.Relay
{
    /// <summary>
    /// Génère des codes de room à 6 caractères (charset sans ambiguïté).
    ///
    /// HISTORIQUE — v1.0 → v1.2.x, ce composant gérait aussi l'enregistrement
    /// HTTP des rooms côté serveur Node.js. À partir de v1.3.0, le relais est
    /// devenu un proxy TCP pur (cf. <c>CoopTcpTransport</c> + <c>relay-server.js</c>),
    /// donc plus aucun appel HTTP. Le code généré sert directement à
    /// l'identification de la session : l'hôte se connecte au relais avec
    /// <c>HOST &lt;code&gt;</c>, l'invité avec <c>JOIN &lt;code&gt;</c>, et le relais les pair.
    /// </summary>
    public class RoomManager : MonoBehaviour
    {
        public static RoomManager Instance { get; private set; }

        // Charset sans caractères ambigus (pas de O/0 ni I/1).
        private const string Charset = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        private void Awake()
        {
            if (Instance != null) { Destroy(this); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public string GenerateRoomCode()
        {
            var rng = new System.Random();
            var sb  = new StringBuilder(6);
            for (int i = 0; i < 6; i++)
                sb.Append(Charset[rng.Next(Charset.Length)]);
            return sb.ToString();
        }
    }
}

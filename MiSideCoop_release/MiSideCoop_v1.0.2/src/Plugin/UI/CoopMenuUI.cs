using System;
using UnityEngine;
using MiSideCoop.Network;
using MiSideCoop.Relay;

namespace MiSideCoop.UI
{
    /// <summary>
    /// Interface utilisateur IMGUI du mod co-op.
    ///
    /// F8 → ouvre/ferme le menu.
    /// Fenêtre draggable avec fond semi-transparent.
    /// États : Main → CreateRoom / JoinRoom → Connected.
    ///
    /// HUD superposé en jeu : rôle, pseudo du partenaire, ping.
    /// </summary>
    public class CoopMenuUI : MonoBehaviour
    {

        // ── États du menu ─────────────────────────────────────────────────────
        private enum MenuState { Closed, Main, CreateRoom, JoinRoom, Connected }
        private MenuState _state = MenuState.Main;   // ← Ouvert dès le démarrage

        // ── Champs de saisie ──────────────────────────────────────────────────
        private string _playerName  = "";
        private string _joinCode    = "";
        private string _roomCode    = "";   // code généré pour l'hôte
        private string _statusMsg   = "";
        private bool   _isConnecting;

        // ── Ping ──────────────────────────────────────────────────────────────
        private float _pingTimer;
        private int   _pingMs;

        // ── Styles GUI ────────────────────────────────────────────────────────
        private GUIStyle _windowStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _codeStyle;
        private GUIStyle _inputStyle;
        private GUIStyle _statusStyle;
        private GUIStyle _hudStyle;
        private bool     _stylesReady;

        // Fenêtre — largeur/hauteur définies dans OnGUI (constantes W, H)

        // ── Lifecycle ─────────────────────────────────────────────────────────
        private void Start()
        {
            _playerName = MiSideCoopPlugin.LocalPlayerName.Value;
        }

        public void ToggleMenu()
        {
            if (_state == MenuState.Closed)
            {
                _state = CoopNetworkManager.Instance?.IsConnected == true
                    ? MenuState.Connected
                    : MenuState.Main;
            }
            else
            {
                _state = MenuState.Closed;
            }
        }

        // ── Update : ping ─────────────────────────────────────────────────────
        private void Update()
        {
            if (CoopNetworkManager.Instance?.IsConnected != true) return;

            _pingTimer += Time.deltaTime;
            if (_pingTimer < 1f) return;
            _pingTimer = 0f;

            try   { _pingMs = -1; /* Ping non disponible sans transport Mirror */ }
            catch { _pingMs = -1; }
        }

        // ── Rendu GUI ─────────────────────────────────────────────────────────
        //
        // IMPORTANT : on N'UTILISE PLUS AUCUNE API GUILayout (BeginArea, Button,
        // Label, TextField, Space…) car Unity les a strippées dans le build
        // IL2CPP de MiSide → "NotSupportedException: Method unstripping failed".
        // Tout est rendu en absolu avec GUI.* (Rect → x,y,w,h).
        // ─────────────────────────────────────────────────────────────────────
        private void OnGUI()
        {
            InitStyles();
            DrawHUD();

            if (_state == MenuState.Closed) return;

            // Fond sombre plein écran
            var oldColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = oldColor;

            // Position centrée
            const float W = 520f, H = 460f;
            float x = (Screen.width  - W) * 0.5f;
            float y = (Screen.height - H) * 0.5f;

            // Cadre + titre
            GUI.Box(new Rect(x, y, W, H), "  MiSide Co-op — Mode Coopératif  ", _windowStyle);

            // Zone contenu (offsets manuels au lieu de BeginArea/EndArea)
            float cx = x + 16f;
            float cy = y + 36f;
            float cw = W - 32f;

            switch (_state)
            {
                case MenuState.Main:       DrawMain(cx, cy, cw);       break;
                case MenuState.CreateRoom: DrawCreateRoom(cx, cy, cw); break;
                case MenuState.JoinRoom:   DrawJoinRoom(cx, cy, cw);   break;
                case MenuState.Connected:  DrawConnected(cx, cy, cw);  break;
            }

            // Message de statut (affiché en bas de la fenêtre)
            if (!string.IsNullOrEmpty(_statusMsg))
            {
                var statusRect = new Rect(cx, y + H - 36f, cw, 24f);
                var statusColor = _statusMsg.StartsWith("Erreur")
                    ? Color.red
                    : new Color(0.4f, 1f, 0.4f);

                var oldTxt = _statusStyle.normal.textColor;
                _statusStyle.normal.textColor = statusColor;
                GUI.Label(statusRect, _statusMsg, _statusStyle);
                _statusStyle.normal.textColor = oldTxt;
            }
        }

        // ── Écran principal ───────────────────────────────────────────────────
        private void DrawMain(float x, float y, float w)
        {
            GUI.Label(new Rect(x, y, w, 40f),
                "Jouez à MiSide à deux dans la même session. Appuyez sur F8 pour fermer.",
                _labelStyle);

            GUI.Label(new Rect(x, y + 52f, w, 22f), "Votre pseudo :", _labelStyle);
            _playerName = GUI.TextField(new Rect(x, y + 76f, w, 36f), _playerName, 24, _inputStyle);

            if (GUI.Button(new Rect(x, y + 130f, w, 46f), "Créer une room  (Hôte)", _buttonStyle))
            {
                SavePlayerName();
                _state = MenuState.CreateRoom;
                CreateRoom();
            }

            if (GUI.Button(new Rect(x, y + 184f, w, 46f), "Rejoindre une room  (Invité)", _buttonStyle))
            {
                SavePlayerName();
                _state = MenuState.JoinRoom;
                _statusMsg = "";
            }
        }

        // ── Écran création de room ────────────────────────────────────────────
        private void DrawCreateRoom(float x, float y, float w)
        {
            if (string.IsNullOrEmpty(_roomCode))
            {
                GUI.Label(new Rect(x, y, w, 30f), "Génération du code…", _labelStyle);
                return;
            }

            GUI.Label(new Rect(x, y, w, 22f), "Votre code de room :", _labelStyle);
            GUI.Label(new Rect(x, y + 28f, w, 66f), _roomCode, _codeStyle);
            GUI.Label(new Rect(x, y + 102f, w, 40f),
                "Partagez ce code avec votre ami. Il peut vous rejoindre depuis le menu Invité.",
                _labelStyle);

            if (GUI.Button(new Rect(x, y + 154f, w, 38f), "Copier le code", _buttonStyle))
            {
                GUIUtility.systemCopyBuffer = _roomCode;
                _statusMsg = "Code copié dans le presse-papiers !";
            }

            if (GUI.Button(new Rect(x, y + 198f, w, 36f),
                "Fermer  (serveur actif en arrière-plan)", _buttonStyle))
                _state = MenuState.Closed;

            if (GUI.Button(new Rect(x, y + 240f, w, 36f),
                "Annuler et fermer la session", _buttonStyle))
                CancelSession();
        }

        // ── Écran rejoindre une room ──────────────────────────────────────────
        private void DrawJoinRoom(float x, float y, float w)
        {
            GUI.Label(new Rect(x, y, w, 22f),
                "Entrez le code de room de l'hôte (6 caractères) :", _labelStyle);

            _joinCode = GUI.TextField(new Rect(x, y + 30f, w, 52f),
                _joinCode.ToUpperInvariant(), 6, _inputStyle);

            if (_isConnecting)
            {
                GUI.Label(new Rect(x, y + 96f, w, 24f), "Connexion en cours…", _statusStyle);
            }
            else
            {
                GUI.enabled = _joinCode.Length == 6;
                if (GUI.Button(new Rect(x, y + 96f, w, 46f), "Rejoindre", _buttonStyle))
                    JoinRoom();
                GUI.enabled = true;
            }

            if (!_isConnecting &&
                GUI.Button(new Rect(x, y + 152f, w, 34f), "Retour", _buttonStyle))
            {
                _state     = MenuState.Main;
                _statusMsg = "";
            }
        }

        // ── Écran connecté ────────────────────────────────────────────────────
        private void DrawConnected(float x, float y, float w)
        {
            var nm = CoopNetworkManager.Instance;
            string role = nm?.IsHost == true ? "Hôte" : "Invité";

            GUI.Label(new Rect(x, y, w, 22f),
                $"Session co-op active  —  Rôle : {role}", _labelStyle);

            if (!string.IsNullOrEmpty(nm?.RoomCode))
                GUI.Label(new Rect(x, y + 28f, w, 22f),
                    $"Code room : {nm.RoomCode}", _labelStyle);

            GUI.Label(new Rect(x, y + 54f, w, 22f),
                $"Joueur distant : {nm?.ConnectedPlayerName ?? "..."}", _labelStyle);
            GUI.Label(new Rect(x, y + 80f, w, 22f),
                $"Ping : {(_pingMs >= 0 ? $"{_pingMs} ms" : "N/A")}", _labelStyle);

            if (GUI.Button(new Rect(x, y + 120f, w, 38f), "Fermer le menu", _buttonStyle))
                _state = MenuState.Closed;

            if (GUI.Button(new Rect(x, y + 164f, w, 38f),
                "Quitter la session co-op", _buttonStyle))
            {
                CoopNetworkManager.Instance?.StopCoop();
                _state   = MenuState.Main;
                _roomCode = "";
                _statusMsg = "Session terminée.";
            }
        }

        // ── HUD en jeu ────────────────────────────────────────────────────────
        private void DrawHUD()
        {
            if (_state != MenuState.Closed) return;
            var nm = CoopNetworkManager.Instance;
            if (nm == null || !nm.IsConnected) return;

            string role = nm.IsHost ? "Hôte" : "Invité";
            string ping = _pingMs >= 0 ? $"{_pingMs}ms" : "N/A";
            string hud  = $"[Co-op] {role}  |  {nm.ConnectedPlayerName}  |  {ping}  |  F8 : menu";

            GUI.Label(new Rect(10, 10, 600, 24), hud, _hudStyle);
        }

        // ── Logique métier ────────────────────────────────────────────────────
        private void CreateRoom()
        {
            _roomCode  = "";
            _statusMsg = "Création de la room…";

            var relay = GetOrAddRoomManager();
            string code = relay.GenerateRoomCode();

            CoopNetworkManager.Instance?.StartHost(code);

            relay.RegisterRoom(code, ok =>
            {
                _roomCode  = code;
                _statusMsg = ok
                    ? "Room créée. En attente de l'invité…"
                    : "Relais indisponible — mode IP directe actif.";
            });
        }

        private void JoinRoom()
        {
            _isConnecting = true;
            _statusMsg    = $"Résolution du code {_joinCode}…";

            GetOrAddRoomManager().ResolveRoom(_joinCode, (ip, port, err) =>
            {
                _isConnecting = false;

                if (!string.IsNullOrEmpty(err))
                {
                    _statusMsg = $"Erreur : {err}";
                    return;
                }

                _statusMsg = $"Connexion à {ip}:{port}…";
                CoopNetworkManager.Instance?.StartClient(ip, port);
                _state = MenuState.Connected;
            });
        }

        private void CancelSession()
        {
            if (!string.IsNullOrEmpty(_roomCode))
                GetOrAddRoomManager().UnregisterRoom(_roomCode);

            CoopNetworkManager.Instance?.StopCoop();
            _state   = MenuState.Main;
            _roomCode = "";
            _statusMsg = "Session annulée.";
        }

        private void SavePlayerName()
        {
            if (!string.IsNullOrWhiteSpace(_playerName))
                MiSideCoopPlugin.LocalPlayerName.Value = _playerName;
        }

        private RoomManager GetOrAddRoomManager()
            => RoomManager.Instance ?? gameObject.AddComponent<RoomManager>();

        // ── Initialisation des styles (appelée une fois depuis OnGUI) ─────────
        private void InitStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            // Fallbacks immédiats : si l'IL2CPP bridge échoue plus bas, on garde
            // au minimum des styles fonctionnels issus du skin par défaut.
            _windowStyle = GUI.skin.window;
            _labelStyle  = GUI.skin.label;
            _buttonStyle = GUI.skin.button;
            _codeStyle   = GUI.skin.label;
            _inputStyle  = GUI.skin.textField;
            _statusStyle = GUI.skin.label;
            _hudStyle    = GUI.skin.label;

            try
            {
                _windowStyle = new GUIStyle(GUI.skin.window)
                {
                    fontSize  = 15,
                    fontStyle = FontStyle.Bold
                };
                _windowStyle.normal.textColor = Color.white;

                _labelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    wordWrap = true
                };
                _labelStyle.normal.textColor = new Color(0.9f, 0.9f, 0.9f);

                _buttonStyle = new GUIStyle(GUI.skin.button)
                {
                    fontSize  = 14,
                    fontStyle = FontStyle.Bold
                    // padding hérité de GUI.skin.button — on évite "new RectOffset(int,int,int,int)"
                    // car ce ctor n'est pas bridgé par Il2CppInterop (MissingMethodException).
                };

                _codeStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize  = 44,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter
                };
                _codeStyle.normal.textColor = new Color(1f, 0.88f, 0.1f);

                _inputStyle = new GUIStyle(GUI.skin.textField)
                {
                    fontSize  = 22,
                    alignment = TextAnchor.MiddleCenter
                };

                _statusStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize  = 12,
                    wordWrap  = true,
                    alignment = TextAnchor.MiddleCenter
                };
                _statusStyle.normal.textColor = new Color(0.5f, 1f, 0.5f);

                _hudStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13
                };
                _hudStyle.normal.textColor = Color.white;
            }
            catch (Exception ex)
            {
                // En IL2CPP, certaines API de GUIStyle peuvent ne pas être bridgées.
                // Si ça arrive : on garde les fallbacks définis plus haut → l'UI reste fonctionnelle.
                BepInEx.Logging.Logger.CreateLogSource("MiSideCoop.UI")
                    .LogWarning($"InitStyles partiel (IL2CPP) : {ex.Message}");
            }
        }
    }
}

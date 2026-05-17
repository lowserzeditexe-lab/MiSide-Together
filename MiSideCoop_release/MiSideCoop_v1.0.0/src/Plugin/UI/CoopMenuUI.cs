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
        private MenuState _state = MenuState.Closed;

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

        // Fenêtre draggable
        private Rect _windowRect = new Rect(0, 0, 520, 420);

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
        private void OnGUI()
        {
            InitStyles();
            DrawHUD();

            if (_state == MenuState.Closed) return;

            // Fond sombre
            var oldColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = oldColor;

            // Centre la fenêtre
            _windowRect.x = (Screen.width  - _windowRect.width)  * 0.5f;
            _windowRect.y = (Screen.height - _windowRect.height) * 0.5f;

            _windowRect = GUI.Window(
                9876, _windowRect, DrawWindow,
                "  MiSide Co-op — Mode Coopératif  ",
                _windowStyle);
        }

        private void DrawWindow(int id)
        {
            GUILayout.Space(8);

            switch (_state)
            {
                case MenuState.Main:       DrawMain();       break;
                case MenuState.CreateRoom: DrawCreateRoom(); break;
                case MenuState.JoinRoom:   DrawJoinRoom();   break;
                case MenuState.Connected:  DrawConnected();  break;
            }

            // Message de statut
            if (!string.IsNullOrEmpty(_statusMsg))
            {
                GUILayout.Space(6);
                var style = new GUIStyle(_statusStyle)
                {
                    normal = { textColor = _statusMsg.StartsWith("Erreur") ? Color.red : new Color(0.4f, 1f, 0.4f) }
                };
                GUILayout.Label(_statusMsg, style);
            }

            GUILayout.Space(6);
            GUI.DragWindow();
        }

        // ── Écran principal ───────────────────────────────────────────────────
        private void DrawMain()
        {
            GUILayout.Label("Jouez à MiSide à deux dans la même session. Appuyez sur F8 pour fermer.", _labelStyle);
            GUILayout.Space(12);

            GUILayout.Label("Votre pseudo :", _labelStyle);
            _playerName = GUILayout.TextField(_playerName, 24, _inputStyle, GUILayout.Height(36));
            GUILayout.Space(10);

            if (GUILayout.Button("Créer une room  (Hôte)", _buttonStyle, GUILayout.Height(46)))
            {
                SavePlayerName();
                _state = MenuState.CreateRoom;
                CreateRoom();
            }

            GUILayout.Space(6);

            if (GUILayout.Button("Rejoindre une room  (Invité)", _buttonStyle, GUILayout.Height(46)))
            {
                SavePlayerName();
                _state = MenuState.JoinRoom;
                _statusMsg = "";
            }
        }

        // ── Écran création de room ────────────────────────────────────────────
        private void DrawCreateRoom()
        {
            if (string.IsNullOrEmpty(_roomCode))
            {
                GUILayout.Label("Génération du code…", _labelStyle);
            }
            else
            {
                GUILayout.Label("Votre code de room :", _labelStyle);
                GUILayout.Space(4);
                GUILayout.Label(_roomCode, _codeStyle, GUILayout.Height(66));
                GUILayout.Space(8);
                GUILayout.Label("Partagez ce code avec votre ami. Il peut vous rejoindre depuis le menu Invité.", _labelStyle);
                GUILayout.Space(10);

                if (GUILayout.Button("Copier le code", _buttonStyle, GUILayout.Height(38)))
                {
                    GUIUtility.systemCopyBuffer = _roomCode;
                    _statusMsg = "Code copié dans le presse-papiers !";
                }

                GUILayout.Space(4);

                if (GUILayout.Button("Fermer  (serveur actif en arrière-plan)", _buttonStyle, GUILayout.Height(36)))
                    _state = MenuState.Closed;

                GUILayout.Space(4);

                if (GUILayout.Button("Annuler et fermer la session", _buttonStyle, GUILayout.Height(36)))
                    CancelSession();
            }
        }

        // ── Écran rejoindre une room ──────────────────────────────────────────
        private void DrawJoinRoom()
        {
            GUILayout.Label("Entrez le code de room de l'hôte (6 caractères) :", _labelStyle);
            GUILayout.Space(8);

            _joinCode = GUILayout.TextField(_joinCode.ToUpperInvariant(), 6, _inputStyle, GUILayout.Height(52));
            GUILayout.Space(10);

            if (_isConnecting)
            {
                GUILayout.Label("Connexion en cours…", _statusStyle);
            }
            else
            {
                GUI.enabled = _joinCode.Length == 6;
                if (GUILayout.Button("Rejoindre", _buttonStyle, GUILayout.Height(46)))
                    JoinRoom();
                GUI.enabled = true;
            }

            GUILayout.Space(6);

            if (!_isConnecting && GUILayout.Button("Retour", _buttonStyle, GUILayout.Height(34)))
            {
                _state     = MenuState.Main;
                _statusMsg = "";
            }
        }

        // ── Écran connecté ────────────────────────────────────────────────────
        private void DrawConnected()
        {
            var nm = CoopNetworkManager.Instance;
            string role = nm?.IsHost == true ? "Hôte" : "Invité";

            GUILayout.Label($"Session co-op active  —  Rôle : {role}", _labelStyle);
            GUILayout.Space(6);

            if (!string.IsNullOrEmpty(nm?.RoomCode))
                GUILayout.Label($"Code room : {nm.RoomCode}", _labelStyle);

            GUILayout.Label($"Joueur distant : {nm?.ConnectedPlayerName ?? "..."}", _labelStyle);
            GUILayout.Label($"Ping : {(_pingMs >= 0 ? $"{_pingMs} ms" : "N/A")}", _labelStyle);

            GUILayout.Space(14);

            if (GUILayout.Button("Fermer le menu", _buttonStyle, GUILayout.Height(38)))
                _state = MenuState.Closed;

            GUILayout.Space(4);

            if (GUILayout.Button("Quitter la session co-op", _buttonStyle, GUILayout.Height(38)))
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
                fontStyle = FontStyle.Bold,
                padding   = new RectOffset(12, 12, 8, 8)
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
    }
}

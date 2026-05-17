using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using MiSideCoop.Network;
using MiSideCoop.Relay;

namespace MiSideCoop.UI
{
    /// <summary>
    /// Interface utilisateur du mod co-op — version uGUI (Canvas + GameObjects).
    ///
    /// Justification du choix uGUI :
    ///   • MiSide est compilé en IL2CPP avec stripping agressif → toutes les API
    ///     IMGUI internes (GUILayout, GUIStateObjects, RectOffset multi-args, etc.)
    ///     sont strippées car le jeu ne s'en sert pas.
    ///   • Le jeu utilise uGUI pour son propre menu → les classes UnityEngine.UI
    ///     (Canvas, Image, Text, Button, InputField) sont GARANTIES présentes.
    ///
    /// Architecture :
    ///   • Un Canvas plein écran (ScreenSpaceOverlay) créé au Start()
    ///   • 4 sous-panels (Main / CreateRoom / JoinRoom / Connected) activés
    ///     via SetActive selon l'état
    ///   • F8 toggle le Canvas root.
    ///   • Boutons branchés via Button.onClick.AddListener(UnityAction)
    ///
    /// API publique conservée :
    ///   • void ToggleMenu()  → appelée par CoopBootstrap sur F8
    /// </summary>
    public class CoopMenuUI : MonoBehaviour
    {
        // ── États ─────────────────────────────────────────────────────────────
        private enum MenuState { Closed, Main, CreateRoom, JoinRoom, Connected }
        private MenuState _state = MenuState.Main;

        // ── Données ───────────────────────────────────────────────────────────
        private string _playerName = "";
        private string _roomCode   = "";
        private bool   _isConnecting;
        private float  _pingTimer;
        private int    _pingMs = -1;

        // ── Hiérarchie UI ─────────────────────────────────────────────────────
        private GameObject _canvasRoot;
        private GameObject _panelMain;
        private GameObject _panelCreate;
        private GameObject _panelJoin;
        private GameObject _panelConnected;

        private InputField _playerNameInput;
        private InputField _joinCodeInput;
        private Text _createRoomCodeText;
        private Text _createStatusText;
        private Text _joinStatusText;
        private Text _connectedInfoText;
        private Text _hudText;
        private GameObject _hudRoot;
        private Button _joinSubmitButton;

        // Police trouvée dans la scène (les ressources built-in sont strippées)
        private Font _font;

        // ── Couleurs ──────────────────────────────────────────────────────────
        private static readonly Color BG_OVERLAY  = new Color(0f, 0f, 0f, 0.72f);
        private static readonly Color PANEL_BG    = new Color(0.10f, 0.10f, 0.13f, 0.96f);
        private static readonly Color PANEL_BORDER= new Color(0.32f, 0.32f, 0.38f, 1f);
        private static readonly Color BTN_NORMAL  = new Color(0.18f, 0.18f, 0.24f, 1f);
        private static readonly Color BTN_HOVER   = new Color(0.28f, 0.28f, 0.36f, 1f);
        private static readonly Color BTN_PRESSED = new Color(0.12f, 0.12f, 0.18f, 1f);
        private static readonly Color INPUT_BG    = new Color(0.06f, 0.06f, 0.10f, 1f);
        private static readonly Color TXT_LIGHT   = new Color(0.92f, 0.92f, 0.95f, 1f);
        private static readonly Color TXT_DIM     = new Color(0.70f, 0.70f, 0.75f, 1f);
        private static readonly Color TXT_ACCENT  = new Color(1f, 0.88f, 0.10f, 1f);
        private static readonly Color TXT_OK      = new Color(0.40f, 1f, 0.40f, 1f);
        private static readonly Color TXT_ERR     = new Color(1f, 0.35f, 0.35f, 1f);

        // ─────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────────
        private void Start()
        {
            _playerName = MiSideCoopPlugin.LocalPlayerName != null
                ? MiSideCoopPlugin.LocalPlayerName.Value
                : "Player2";

            try
            {
                EnsureEventSystem();
                _font = FindFont();
                BuildCanvas();
                ShowState(MenuState.Main);
                MiSideCoopPlugin.Logger?.LogInfo("[Co-op] UI uGUI construite avec succès.");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogError($"[Co-op] Échec construction UI : {ex}");
            }
        }

        private void OnDestroy()
        {
            if (_canvasRoot != null) Destroy(_canvasRoot);
        }

        public void ToggleMenu()
        {
            if (_canvasRoot == null) return;

            if (_state == MenuState.Closed)
            {
                ShowState(CoopNetworkManager.Instance?.IsConnected == true
                    ? MenuState.Connected
                    : MenuState.Main);
            }
            else
            {
                ShowState(MenuState.Closed);
            }
        }

        private void Update()
        {
            // Mise à jour du HUD ping si connecté
            var nm = CoopNetworkManager.Instance;
            if (nm == null) return;

            if (nm.IsConnected)
            {
                _pingTimer += Time.deltaTime;
                if (_pingTimer >= 1f)
                {
                    _pingTimer = 0f;
                    _pingMs = -1;
                }
            }

            UpdateHud();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Construction du Canvas et de tous les panels
        // ─────────────────────────────────────────────────────────────────────
        private void BuildCanvas()
        {
            // Root Canvas
            _canvasRoot = new GameObject("MiSideCoopCanvas");
            _canvasRoot.transform.SetParent(null);
            DontDestroyOnLoad(_canvasRoot);

            var canvas = _canvasRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000; // au-dessus du HUD du jeu

            var scaler = _canvasRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            _canvasRoot.AddComponent<GraphicRaycaster>();

            // Overlay sombre plein écran
            var overlay = CreateImage(_canvasRoot, "Overlay", BG_OVERLAY);
            StretchToParent(overlay.GetComponent<RectTransform>());

            // HUD en jeu (texte petit en haut-gauche)
            _hudRoot = new GameObject("HUD");
            _hudRoot.transform.SetParent(_canvasRoot.transform, false);
            var hudRt = _hudRoot.AddComponent<RectTransform>();
            hudRt.anchorMin = new Vector2(0f, 1f);
            hudRt.anchorMax = new Vector2(0f, 1f);
            hudRt.pivot     = new Vector2(0f, 1f);
            hudRt.anchoredPosition = new Vector2(20f, -20f);
            hudRt.sizeDelta = new Vector2(800f, 32f);
            _hudText = AddText(_hudRoot, "", 18, TXT_LIGHT, TextAnchor.MiddleLeft);
            _hudRoot.SetActive(false);

            // Fenêtre centrale (cadre + sous-panels)
            var window = new GameObject("Window");
            window.transform.SetParent(_canvasRoot.transform, false);
            var winRt = window.AddComponent<RectTransform>();
            winRt.anchorMin = new Vector2(0.5f, 0.5f);
            winRt.anchorMax = new Vector2(0.5f, 0.5f);
            winRt.pivot     = new Vector2(0.5f, 0.5f);
            winRt.sizeDelta = new Vector2(620f, 540f);
            var winImg = window.AddComponent<Image>();
            winImg.color = PANEL_BG;

            // Bordure (image légèrement plus grande derrière)
            var border = CreateImage(window, "Border", PANEL_BORDER);
            var borderRt = border.GetComponent<RectTransform>();
            borderRt.anchorMin = Vector2.zero;
            borderRt.anchorMax = Vector2.one;
            borderRt.offsetMin = new Vector2(-2f, -2f);
            borderRt.offsetMax = new Vector2(2f, 2f);
            border.transform.SetAsFirstSibling();

            // Titre
            var titleObj = new GameObject("Title");
            titleObj.transform.SetParent(window.transform, false);
            var titleRt = titleObj.AddComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot     = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, -18f);
            titleRt.sizeDelta = new Vector2(-40f, 30f);
            AddText(titleObj, "MiSide Co-op — Mode Coopératif", 22, TXT_ACCENT, TextAnchor.MiddleCenter);

            // Sous-panels (Main / Create / Join / Connected)
            _panelMain      = BuildPanelMain(window);
            _panelCreate    = BuildPanelCreate(window);
            _panelJoin      = BuildPanelJoin(window);
            _panelConnected = BuildPanelConnected(window);
        }

        // ── Sous-panels ──────────────────────────────────────────────────────
        private GameObject BuildPanelMain(GameObject parent)
        {
            var p = MakePanelHolder(parent, "PanelMain");

            AddLabel(p,  0f, "Jouez à MiSide à deux dans la même session.",
                     16, TXT_DIM, TextAnchor.MiddleCenter);
            AddLabel(p, 28f, "Appuyez sur F8 pour fermer ce menu.",
                     14, TXT_DIM, TextAnchor.MiddleCenter);

            AddLabel(p, 80f, "Votre pseudo :", 16, TXT_LIGHT, TextAnchor.MiddleLeft);
            _playerNameInput = AddInputField(p, 110f, _playerName, 24, "Entrez votre pseudo…");
            _playerNameInput.onValueChanged.AddListener(new UnityAction<string>(v => _playerName = v));

            AddButton(p, 170f, "Créer une room  (Hôte)", new UnityAction(() =>
            {
                SavePlayerName();
                ShowState(MenuState.CreateRoom);
                CreateRoom();
            }));

            AddButton(p, 240f, "Rejoindre une room  (Invité)", new UnityAction(() =>
            {
                SavePlayerName();
                ShowState(MenuState.JoinRoom);
            }));

            return p;
        }

        private GameObject BuildPanelCreate(GameObject parent)
        {
            var p = MakePanelHolder(parent, "PanelCreate");

            AddLabel(p, 0f, "Votre code de room :", 16, TXT_LIGHT, TextAnchor.MiddleCenter);

            // Code de room (gros, au centre)
            var codeObj = new GameObject("Code");
            codeObj.transform.SetParent(p.transform, false);
            var codeRt = codeObj.AddComponent<RectTransform>();
            codeRt.anchorMin = new Vector2(0f, 1f);
            codeRt.anchorMax = new Vector2(1f, 1f);
            codeRt.pivot     = new Vector2(0.5f, 1f);
            codeRt.anchoredPosition = new Vector2(0f, -34f);
            codeRt.sizeDelta = new Vector2(-20f, 90f);
            _createRoomCodeText = AddText(codeObj, "------", 56, TXT_ACCENT, TextAnchor.MiddleCenter);

            AddLabel(p, 140f, "Partagez ce code avec votre ami. Il pourra vous rejoindre depuis le menu Invité.",
                     14, TXT_DIM, TextAnchor.MiddleCenter);

            AddButton(p, 200f, "Copier le code", new UnityAction(() =>
            {
                if (!string.IsNullOrEmpty(_roomCode))
                {
                    GUIUtility.systemCopyBuffer = _roomCode;
                    SetStatus(_createStatusText, "Code copié dans le presse-papiers !", TXT_OK);
                }
            }));

            AddButton(p, 260f, "Fermer  (serveur actif en arrière-plan)", new UnityAction(() =>
            {
                ShowState(MenuState.Closed);
            }));

            AddButton(p, 320f, "Annuler et fermer la session", new UnityAction(CancelSession));

            _createStatusText = AddLabel(p, 380f, "", 14, TXT_DIM, TextAnchor.MiddleCenter);

            return p;
        }

        private GameObject BuildPanelJoin(GameObject parent)
        {
            var p = MakePanelHolder(parent, "PanelJoin");

            AddLabel(p, 0f, "Entrez le code de room de l'hôte (6 caractères) :",
                     16, TXT_LIGHT, TextAnchor.MiddleCenter);

            _joinCodeInput = AddInputField(p, 40f, "", 6, "ABC123");
            _joinCodeInput.characterLimit = 6;
            // force majuscules
            _joinCodeInput.onValueChanged.AddListener(new UnityAction<string>(v =>
            {
                var upper = (v ?? "").ToUpperInvariant();
                if (upper != v)
                {
                    _joinCodeInput.text = upper;
                    return;
                }
                if (_joinSubmitButton != null)
                    _joinSubmitButton.interactable = upper.Length == 6 && !_isConnecting;
            }));
            // Texte plus gros pour le code
            var jciTxt = _joinCodeInput.textComponent;
            if (jciTxt != null)
            {
                jciTxt.fontSize = 32;
                jciTxt.alignment = TextAnchor.MiddleCenter;
            }

            _joinSubmitButton = AddButton(p, 120f, "Rejoindre", new UnityAction(JoinRoom));
            _joinSubmitButton.interactable = false;

            AddButton(p, 190f, "Retour", new UnityAction(() => ShowState(MenuState.Main)));

            _joinStatusText = AddLabel(p, 260f, "", 14, TXT_DIM, TextAnchor.MiddleCenter);

            return p;
        }

        private GameObject BuildPanelConnected(GameObject parent)
        {
            var p = MakePanelHolder(parent, "PanelConnected");

            _connectedInfoText = AddLabel(p, 0f, "", 16, TXT_LIGHT, TextAnchor.MiddleLeft);
            // Hauteur étendue pour 4 lignes
            var rt = _connectedInfoText.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, 110f);

            AddButton(p, 140f, "Fermer le menu", new UnityAction(() => ShowState(MenuState.Closed)));

            AddButton(p, 210f, "Quitter la session co-op", new UnityAction(() =>
            {
                CoopNetworkManager.Instance?.StopCoop();
                _roomCode = "";
                ShowState(MenuState.Main);
                SetStatus(_createStatusText, "Session terminée.", TXT_DIM);
            }));

            return p;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers de construction UI
        // ─────────────────────────────────────────────────────────────────────
        private GameObject MakePanelHolder(GameObject parent, string name)
        {
            var p = new GameObject(name);
            p.transform.SetParent(parent.transform, false);
            var rt = p.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.offsetMin = new Vector2(28f, 28f);
            rt.offsetMax = new Vector2(-28f, -58f); // espace réservé au titre en haut
            p.SetActive(false);
            return p;
        }

        private Image CreateImage(GameObject parent, string name, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<RectTransform>();
            var img = go.AddComponent<Image>();
            img.color = color;
            return img;
        }

        private void StretchToParent(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private Text AddText(GameObject parent, string content, int fontSize, Color color, TextAnchor anchor)
        {
            var txt = parent.AddComponent<Text>();
            txt.text = content;
            txt.color = color;
            txt.fontSize = fontSize;
            txt.alignment = anchor;
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            if (_font != null) txt.font = _font;
            return txt;
        }

        /// <summary>Crée un GameObject enfant avec un Text centré, à l'offset y donné (depuis le haut).</summary>
        private Text AddLabel(GameObject parent, float yFromTop, string content, int fontSize, Color color, TextAnchor anchor)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -yFromTop);
            rt.sizeDelta = new Vector2(0f, 30f);
            return AddText(go, content, fontSize, color, anchor);
        }

        private InputField AddInputField(GameObject parent, float yFromTop, string initial, int maxLength, string placeholder)
        {
            // Conteneur
            var go = new GameObject("InputField");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -yFromTop);
            rt.sizeDelta = new Vector2(0f, 44f);

            var bg = go.AddComponent<Image>();
            bg.color = INPUT_BG;

            // Texte affiché
            var textObj = new GameObject("Text");
            textObj.transform.SetParent(go.transform, false);
            var textRt = textObj.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(10f, 4f);
            textRt.offsetMax = new Vector2(-10f, -4f);
            var text = AddText(textObj, "", 18, TXT_LIGHT, TextAnchor.MiddleLeft);
            text.supportRichText = false;

            // Placeholder
            var phObj = new GameObject("Placeholder");
            phObj.transform.SetParent(go.transform, false);
            var phRt = phObj.AddComponent<RectTransform>();
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(10f, 4f);
            phRt.offsetMax = new Vector2(-10f, -4f);
            var ph = AddText(phObj, placeholder, 18, new Color(0.5f, 0.5f, 0.55f, 1f), TextAnchor.MiddleLeft);
            ph.fontStyle = FontStyle.Italic;

            var input = go.AddComponent<InputField>();
            input.textComponent = text;
            input.placeholder   = ph;
            input.characterLimit = maxLength;
            input.text = initial ?? "";

            return input;
        }

        private Button AddButton(GameObject parent, float yFromTop, string label, UnityAction onClick)
        {
            var go = new GameObject("Button_" + label);
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -yFromTop);
            rt.sizeDelta = new Vector2(0f, 56f);

            var img = go.AddComponent<Image>();
            img.color = BTN_NORMAL;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.normalColor      = BTN_NORMAL;
            colors.highlightedColor = BTN_HOVER;
            colors.pressedColor     = BTN_PRESSED;
            colors.selectedColor    = BTN_HOVER;
            colors.disabledColor    = new Color(0.12f, 0.12f, 0.15f, 0.8f);
            btn.colors = colors;

            // Label
            var labelObj = new GameObject("Label");
            labelObj.transform.SetParent(go.transform, false);
            var lblRt = labelObj.AddComponent<RectTransform>();
            lblRt.anchorMin = Vector2.zero;
            lblRt.anchorMax = Vector2.one;
            lblRt.offsetMin = Vector2.zero;
            lblRt.offsetMax = Vector2.zero;
            var lblTxt = AddText(labelObj, label, 18, TXT_LIGHT, TextAnchor.MiddleCenter);
            lblTxt.fontStyle = FontStyle.Bold;

            btn.onClick.AddListener(onClick);
            return btn;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Gestion d'état UI
        // ─────────────────────────────────────────────────────────────────────
        private void ShowState(MenuState newState)
        {
            _state = newState;

            bool open = newState != MenuState.Closed;
            if (_canvasRoot != null) _canvasRoot.SetActive(true); // canvas toujours actif pour le HUD

            // Active uniquement le sous-panel correspondant + l'overlay
            SetPanelsActive(open);
            if (_panelMain != null)      _panelMain.SetActive(newState == MenuState.Main);
            if (_panelCreate != null)    _panelCreate.SetActive(newState == MenuState.CreateRoom);
            if (_panelJoin != null)      _panelJoin.SetActive(newState == MenuState.JoinRoom);
            if (_panelConnected != null) _panelConnected.SetActive(newState == MenuState.Connected);

            if (newState == MenuState.Connected) RefreshConnectedInfo();
        }

        /// <summary>Cache/montre l'overlay + la fenêtre (mais le HUD reste indépendant).</summary>
        private void SetPanelsActive(bool open)
        {
            if (_canvasRoot == null) return;

            // Overlay = enfant 0
            for (int i = 0; i < _canvasRoot.transform.childCount; i++)
            {
                var child = _canvasRoot.transform.GetChild(i).gameObject;
                if (child.name == "Overlay" || child.name == "Window")
                    child.SetActive(open);
            }
        }

        private void SetStatus(Text target, string msg, Color color)
        {
            if (target == null) return;
            target.text = msg;
            target.color = color;
        }

        private void RefreshConnectedInfo()
        {
            if (_connectedInfoText == null) return;
            var nm = CoopNetworkManager.Instance;
            if (nm == null)
            {
                _connectedInfoText.text = "(pas de gestionnaire réseau)";
                return;
            }

            string role = nm.IsHost ? "Hôte" : "Invité";
            string remote = string.IsNullOrEmpty(nm.ConnectedPlayerName) ? "..." : nm.ConnectedPlayerName;
            string code = string.IsNullOrEmpty(nm.RoomCode) ? "(aucun)" : nm.RoomCode;
            string ping = _pingMs >= 0 ? $"{_pingMs} ms" : "N/A";

            _connectedInfoText.text =
                $"Session co-op active  —  Rôle : {role}\n" +
                $"Code room : {code}\n" +
                $"Joueur distant : {remote}\n" +
                $"Ping : {ping}";
        }

        private void UpdateHud()
        {
            if (_hudRoot == null) return;
            var nm = CoopNetworkManager.Instance;
            bool show = _state == MenuState.Closed && nm != null && nm.IsConnected;
            _hudRoot.SetActive(show);
            if (!show) return;

            string role = nm.IsHost ? "Hôte" : "Invité";
            string ping = _pingMs >= 0 ? $"{_pingMs}ms" : "N/A";
            _hudText.text = $"[Co-op] {role}  |  {nm.ConnectedPlayerName}  |  {ping}  |  F8 : menu";
        }

        // ─────────────────────────────────────────────────────────────────────
        // Logique métier
        // ─────────────────────────────────────────────────────────────────────
        private void CreateRoom()
        {
            _roomCode = "";
            if (_createRoomCodeText != null) _createRoomCodeText.text = "------";
            SetStatus(_createStatusText, "Création de la room…", TXT_DIM);

            var relay = GetOrAddRoomManager();
            string code = relay.GenerateRoomCode();

            CoopNetworkManager.Instance?.StartHost(code);

            relay.RegisterRoom(code, ok =>
            {
                _roomCode = code;
                if (_createRoomCodeText != null) _createRoomCodeText.text = code;
                SetStatus(_createStatusText,
                    ok ? "Room créée. En attente de l'invité…"
                       : "Relais indisponible — mode IP directe actif.",
                    ok ? TXT_OK : TXT_ERR);
            });
        }

        private void JoinRoom()
        {
            var code = (_joinCodeInput?.text ?? "").ToUpperInvariant();
            if (code.Length != 6) return;

            _isConnecting = true;
            if (_joinSubmitButton != null) _joinSubmitButton.interactable = false;
            SetStatus(_joinStatusText, $"Résolution du code {code}…", TXT_DIM);

            GetOrAddRoomManager().ResolveRoom(code, (ip, port, err) =>
            {
                _isConnecting = false;

                if (!string.IsNullOrEmpty(err))
                {
                    SetStatus(_joinStatusText, $"Erreur : {err}", TXT_ERR);
                    if (_joinSubmitButton != null) _joinSubmitButton.interactable = true;
                    return;
                }

                SetStatus(_joinStatusText, $"Connexion à {ip}:{port}…", TXT_OK);
                CoopNetworkManager.Instance?.StartClient(ip, port);
                ShowState(MenuState.Connected);
            });
        }

        private void CancelSession()
        {
            if (!string.IsNullOrEmpty(_roomCode))
                GetOrAddRoomManager().UnregisterRoom(_roomCode);

            CoopNetworkManager.Instance?.StopCoop();
            _roomCode = "";
            ShowState(MenuState.Main);
            SetStatus(_createStatusText, "Session annulée.", TXT_DIM);
        }

        private void SavePlayerName()
        {
            if (!string.IsNullOrWhiteSpace(_playerName) && MiSideCoopPlugin.LocalPlayerName != null)
                MiSideCoopPlugin.LocalPlayerName.Value = _playerName;
        }

        private RoomManager GetOrAddRoomManager()
            => RoomManager.Instance ?? gameObject.AddComponent<RoomManager>();

        // ─────────────────────────────────────────────────────────────────────
        // Utilitaires runtime IL2CPP-safe
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Trouve une police déjà chargée dans la scène.
        /// Les ressources built-in (Arial.ttf, LegacyRuntime.ttf) sont strippées
        /// par IL2CPP → on récupère une Font utilisée par le jeu lui-même.
        /// </summary>
        private static Font FindFont()
        {
            try
            {
                var fonts = Resources.FindObjectsOfTypeAll<Font>();
                if (fonts != null && fonts.Length > 0)
                    return fonts.FirstOrDefault(f => f != null);
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning($"[Co-op] FindFont échec : {ex.Message}");
            }
            return null;
        }

        /// <summary>S'assure qu'un EventSystem est présent (sinon les Buttons ne reçoivent pas de clics).</summary>
        private static void EnsureEventSystem()
        {
            try
            {
                if (EventSystem.current != null) return;
                var es = new GameObject("MiSideCoopEventSystem");
                DontDestroyOnLoad(es);
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
                MiSideCoopPlugin.Logger?.LogInfo("[Co-op] EventSystem créé (fallback).");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning($"[Co-op] EnsureEventSystem : {ex.Message}");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using MiSideCoop.Network;
using MiSideCoop.Relay;

namespace MiSideCoop.UI
{
    /// <summary>
    /// Interface utilisateur du mod co-op — version uGUI sans delegates.
    ///
    /// CONTRAINTES IL2CPP CONNUES (MiSide / Unity 2021.3 stripping agressif) :
    ///   ✘ Toutes les API IMGUI internes (GUILayout, GUIStateObjects, RectOffset multi-args)
    ///   ✘ Resources.FindObjectsOfTypeAll&lt;T&gt;() et version non-générique (Type)
    ///   ✘ Object.FindObjectOfType(Type)
    ///   ✘ Constructeurs de UnityAction et UnityAction&lt;T&gt; (..ctor(Object, IntPtr))
    ///
    /// CONSÉQUENCE → on évite COMPLÈTEMENT :
    ///   ✘ Button.onClick.AddListener(...)
    ///   ✘ InputField.onValueChanged.AddListener(...)
    ///   ✘ Tout new UnityAction / UnityAction&lt;T&gt;
    ///   ✘ Toute recherche dynamique d'objets via FindObject*
    ///
    /// APPROCHE :
    ///   • Boutons = simples Image+Text. Détection de clic manuelle dans Update()
    ///     via RectTransformUtility.RectangleContainsScreenPoint + Input.GetMouseButtonDown(0)
    ///   • InputField utilisé tel quel, mais on lit .text en polling
    ///   • Police chargée via Font.CreateDynamicFontFromOSFont (système Windows)
    ///   • EventSystem créé sans détection (le jeu en a déjà un, on saute)
    /// </summary>
    public class CoopMenuUI : MonoBehaviour
    {
        // ── États ─────────────────────────────────────────────────────────────
        private enum MenuState { Closed, Main, CreateRoom, JoinRoom, Connected }
        private MenuState _state = MenuState.Main;

        // ── Données ───────────────────────────────────────────────────────────
        private string _playerName  = "";
        private string _roomCode    = "";
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

        private Font _font;
        private bool _fontApplied;     // true quand on a déjà repropagé la font à tous nos Text
        private int  _fontSearchTries; // limite les tentatives de recherche dans Update

        /// <summary>Bouton custom (Image + Text + Action) géré sans Button/UnityAction.</summary>
        private class CustomButton
        {
            public RectTransform Rect;
            public Image Bg;
            public Action OnClick;
            public bool Interactable = true;
            public GameObject Owner; // panel parent : on n'agit que si actif
        }

        private readonly List<CustomButton> _buttons = new List<CustomButton>();
        private CustomButton _joinSubmit;

        // ── Couleurs ──────────────────────────────────────────────────────────
        private static readonly Color BG_OVERLAY   = new Color(0f, 0f, 0f, 0.72f);
        private static readonly Color PANEL_BG     = new Color(0.10f, 0.10f, 0.13f, 0.96f);
        private static readonly Color PANEL_BORDER = new Color(0.32f, 0.32f, 0.38f, 1f);
        private static readonly Color BTN_NORMAL   = new Color(0.18f, 0.18f, 0.24f, 1f);
        private static readonly Color BTN_HOVER    = new Color(0.28f, 0.28f, 0.36f, 1f);
        private static readonly Color BTN_PRESSED  = new Color(0.12f, 0.12f, 0.18f, 1f);
        private static readonly Color BTN_DISABLED = new Color(0.12f, 0.12f, 0.15f, 0.8f);
        private static readonly Color INPUT_BG     = new Color(0.06f, 0.06f, 0.10f, 1f);
        private static readonly Color TXT_LIGHT    = new Color(0.92f, 0.92f, 0.95f, 1f);
        private static readonly Color TXT_DIM      = new Color(0.70f, 0.70f, 0.75f, 1f);
        private static readonly Color TXT_ACCENT   = new Color(1f, 0.88f, 0.10f, 1f);
        private static readonly Color TXT_OK       = new Color(0.40f, 1f, 0.40f, 1f);
        private static readonly Color TXT_ERR      = new Color(1f, 0.35f, 0.35f, 1f);

        // ─────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────────
        private void Start()
        {
            _playerName = MiSideCoopPlugin.LocalPlayerName != null
                ? MiSideCoopPlugin.LocalPlayerName.Value
                : "Player2";

            // Pas de tentative de Font dans Start() — Font.CreateDynamicFontFromOSFont
            // est strippé en IL2CPP. La font sera récupérée dans Update() en parcourant
            // les Text components existants du jeu (cf. TryFindFontInScene).
            try
            {
                BuildCanvas();
                ShowState(MenuState.Main);
                MiSideCoopPlugin.Logger?.LogInfo("[Co-op] UI uGUI construite. Recherche de la font en cours…");
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
                ShowState(CoopNetworkManager.Instance?.IsConnected == true
                    ? MenuState.Connected
                    : MenuState.Main);
            else
                ShowState(MenuState.Closed);
        }

        private void Update()
        {
            // 0. Si pas encore de font, tente de la trouver dans la scène (jeu en cours).
            //    Limité à 30 tentatives pour ne pas spammer si vraiment indisponible.
            if (_font == null && _fontSearchTries < 30)
            {
                _fontSearchTries++;
                Font found = null;
                try
                {
                    found = TryFindFontInScene();
                }
                catch (Exception ex)
                {
                    // En cas d'échec JIT IL2CPP, on stoppe la recherche immédiatement
                    // pour ne pas spammer le log à chaque frame.
                    _fontSearchTries = 999;
                    MiSideCoopPlugin.Logger?.LogWarning($"[Co-op] Recherche font abandonnée (IL2CPP) : {ex.Message}");
                }
                if (found != null)
                {
                    _font = found;
                    ApplyFontEverywhere();
                    MiSideCoopPlugin.Logger?.LogInfo($"[Co-op] Font trouvée dans la scène : {_font.name}");
                }
            }
            else if (_font != null && !_fontApplied)
            {
                ApplyFontEverywhere();
            }

            // 1. Polling des inputs + état des boutons dépendant des inputs
            PollInputs();

            // 2. Détection de clics sur les boutons custom
            HandleButtonClicks();

            // 3. HUD ping
            var nm = CoopNetworkManager.Instance;
            if (nm != null && nm.IsConnected)
            {
                _pingTimer += Time.deltaTime;
                if (_pingTimer >= 1f) { _pingTimer = 0f; _pingMs = -1; }
            }
            UpdateHud();
        }

        /// <summary>
        /// Cherche une Font utilisable.
        ///
        /// HISTORIQUE — Voie A originale via <c>Scene.GetRootGameObjects()</c> :
        /// abandonnée car cette API est STRIPPÉE dans le build IL2CPP de MiSide
        /// (Unity 2021.3 stripping agressif → MissingMethodException dès le 1er appel,
        /// puis "DontDestroyOnLoad only works for root GameObjects" en cascade).
        ///
        /// Voie A (nouvelle) : <c>Resources.GetBuiltinResource&lt;Font&gt;</c> charge les
        /// polices intégrées au moteur Unity (toujours présentes, ne dépendent pas
        /// du stripping IL2CPP côté code utilisateur).
        ///   • Unity 2021.3+   → "LegacyRuntime.ttf"
        ///   • Anciens Unity    → "Arial.ttf"
        ///
        /// Voie B : scan de la hiérarchie sous <c>Camera.main.transform.root</c> pour
        /// récupérer une font déjà utilisée par le jeu (filet de sécurité ultime).
        /// </summary>
        private static Font TryFindFontInScene()
        {
            // Voie A : built-in Unity fonts (immune au stripping IL2CPP)
            var f = SafeGetBuiltinFont("LegacyRuntime.ttf");
            if (f != null) return f;
            f = SafeGetBuiltinFont("Arial.ttf");
            if (f != null) return f;

            // Voie B : depuis Camera.main.transform.root
            var camRoot = SafeGetMainCameraRoot();
            if (camRoot != null)
            {
                f = ScanTransformForFont(camRoot, 0);
                if (f != null) return f;
            }

            return null;
        }

        /// <summary>Charge une police intégrée Unity, en silence si l'API est strippée.</summary>
        private static Font SafeGetBuiltinFont(string fontName)
        {
            try { return Resources.GetBuiltinResource<Font>(fontName); }
            catch { return null; }
        }

        /// <summary>Appel isolé pour Camera.main.transform.root.</summary>
        private static Transform SafeGetMainCameraRoot()
        {
            try
            {
                var cam = Camera.main;
                return cam != null ? cam.transform.root : null;
            }
            catch { return null; }
        }

        /// <summary>Récursion limitée en profondeur sur les enfants à la recherche d'un Text avec font.</summary>
        private static Font ScanTransformForFont(Transform t, int depth)
        {
            if (t == null || depth > 8) return null;

            try
            {
                var txtComp = t.GetComponent(typeof(Text)) as Text;
                if (txtComp != null && txtComp.font != null)
                    return txtComp.font;
            }
            catch { /* GetComponent peut bouger en IL2CPP — on continue */ }

            int count = t.childCount;
            for (int i = 0; i < count; i++)
            {
                var f = ScanTransformForFont(t.GetChild(i), depth + 1);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>Applique _font à tous nos Text déjà créés (rétroactif).</summary>
        private void ApplyFontEverywhere()
        {
            if (_canvasRoot == null || _font == null) return;
            try
            {
                ApplyFontRecursive(_canvasRoot.transform);
                _fontApplied = true;
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning($"[Co-op] ApplyFontEverywhere : {ex.Message}");
            }
        }

        private void ApplyFontRecursive(Transform t)
        {
            if (t == null) return;
            var txtComp = t.GetComponent(typeof(Text)) as Text;
            if (txtComp != null) txtComp.font = _font;

            for (int i = 0; i < t.childCount; i++)
                ApplyFontRecursive(t.GetChild(i));
        }

        private void PollInputs()
        {
            if (_playerNameInput != null)
            {
                var val = _playerNameInput.text;
                if (!string.IsNullOrEmpty(val) && val != _playerName)
                    _playerName = val;
            }

            if (_joinCodeInput != null)
            {
                var v = _joinCodeInput.text ?? "";
                var upper = v.ToUpperInvariant();
                if (upper != v) _joinCodeInput.text = upper;

                if (_joinSubmit != null)
                    _joinSubmit.Interactable = upper.Length == 6 && !_isConnecting;
            }
        }

        private void HandleButtonClicks()
        {
            if (_canvasRoot == null || !_canvasRoot.activeInHierarchy) return;

            bool mouseDown = Input.GetMouseButtonDown(0);
            Vector2 mousePos = Input.mousePosition;

            for (int i = 0; i < _buttons.Count; i++)
            {
                var b = _buttons[i];
                if (b.Rect == null || b.Owner == null) continue;
                if (!b.Owner.activeInHierarchy) continue;

                bool hovered = false;
                try
                {
                    hovered = RectTransformUtility.RectangleContainsScreenPoint(
                        b.Rect, mousePos, null);
                }
                catch { hovered = false; }

                // Coloration selon l'état
                if (b.Bg != null)
                {
                    if (!b.Interactable) b.Bg.color = BTN_DISABLED;
                    else if (hovered && Input.GetMouseButton(0)) b.Bg.color = BTN_PRESSED;
                    else if (hovered) b.Bg.color = BTN_HOVER;
                    else b.Bg.color = BTN_NORMAL;
                }

                // Déclenche le clic
                if (hovered && mouseDown && b.Interactable && b.OnClick != null)
                {
                    try { b.OnClick(); }
                    catch (Exception ex)
                    {
                        MiSideCoopPlugin.Logger?.LogError($"[Co-op] OnClick crash : {ex}");
                    }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Construction du Canvas
        // ─────────────────────────────────────────────────────────────────────
        private void BuildCanvas()
        {
            _canvasRoot = new GameObject("MiSideCoopCanvas");

            // ─── Persistance ─────────────────────────────────────────────────
            // On parente le Canvas au bootstrap (lui-même DontDestroyOnLoad).
            // Avantages :
            //   • Pas de second appel DontDestroyOnLoad sur le Canvas → silence
            //     définitif du warning Unity "DontDestroyOnLoad only works for
            //     root GameObjects" (observé sur certains builds IL2CPP où la
            //     vérification interne est plus stricte).
            //   • Le Canvas hérite automatiquement de la persistance via le
            //     parent (comportement standard Unity).
            //   • ScreenSpaceOverlay reste correctement rendu : ce mode est
            //     indépendant de la position du Canvas dans la hiérarchie.
            // worldPositionStays=false évite tout décalage de RectTransform.
            _canvasRoot.transform.SetParent(this.transform, false);

            var canvas = _canvasRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;

            var scaler = _canvasRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            // Overlay sombre plein écran
            var overlay = CreateImage(_canvasRoot, "Overlay", BG_OVERLAY);
            StretchToParent(overlay.GetComponent<RectTransform>());

            // HUD discret en haut-gauche
            _hudRoot = new GameObject("HUD");
            _hudRoot.transform.SetParent(_canvasRoot.transform, false);
            var hudRt = _hudRoot.AddComponent<RectTransform>();
            hudRt.anchorMin = hudRt.anchorMax = new Vector2(0f, 1f);
            hudRt.pivot     = new Vector2(0f, 1f);
            hudRt.anchoredPosition = new Vector2(20f, -20f);
            hudRt.sizeDelta = new Vector2(800f, 32f);
            _hudText = AddText(_hudRoot, "", 18, TXT_LIGHT, TextAnchor.MiddleLeft);
            _hudRoot.SetActive(false);

            // Fenêtre centrale
            var window = new GameObject("Window");
            window.transform.SetParent(_canvasRoot.transform, false);
            var winRt = window.AddComponent<RectTransform>();
            winRt.anchorMin = winRt.anchorMax = new Vector2(0.5f, 0.5f);
            winRt.pivot = new Vector2(0.5f, 0.5f);
            winRt.sizeDelta = new Vector2(620f, 540f);
            var winImg = window.AddComponent<Image>();
            winImg.color = PANEL_BG;

            var border = CreateImage(window, "Border", PANEL_BORDER);
            var borderRt = border.GetComponent<RectTransform>();
            borderRt.anchorMin = Vector2.zero; borderRt.anchorMax = Vector2.one;
            borderRt.offsetMin = new Vector2(-2f, -2f);
            borderRt.offsetMax = new Vector2(2f, 2f);
            border.transform.SetAsFirstSibling();

            // Titre
            var titleObj = new GameObject("Title");
            titleObj.transform.SetParent(window.transform, false);
            var titleRt = titleObj.AddComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, -18f);
            titleRt.sizeDelta = new Vector2(-40f, 30f);
            AddText(titleObj, "MiSide Co-op — Mode Coopératif", 22, TXT_ACCENT, TextAnchor.MiddleCenter);

            _panelMain      = BuildPanelMain(window);
            _panelCreate    = BuildPanelCreate(window);
            _panelJoin      = BuildPanelJoin(window);
            _panelConnected = BuildPanelConnected(window);
        }

        private GameObject BuildPanelMain(GameObject parent)
        {
            var p = MakePanelHolder(parent, "PanelMain");

            AddLabel(p,  0f, "Jouez à MiSide à deux dans la même session.", 16, TXT_DIM, TextAnchor.MiddleCenter);
            AddLabel(p, 28f, "Appuyez sur F8 pour fermer ce menu.",         14, TXT_DIM, TextAnchor.MiddleCenter);

            AddLabel(p, 80f, "Votre pseudo :", 16, TXT_LIGHT, TextAnchor.MiddleLeft);
            _playerNameInput = AddInputField(p, 110f, _playerName, 24, "Entrez votre pseudo…");

            AddCustomButton(p, 170f, "Créer une room  (Hôte)", () =>
            {
                SavePlayerName();
                ShowState(MenuState.CreateRoom);
                CreateRoom();
            });

            AddCustomButton(p, 240f, "Rejoindre une room  (Invité)", () =>
            {
                SavePlayerName();
                ShowState(MenuState.JoinRoom);
            });

            return p;
        }

        private GameObject BuildPanelCreate(GameObject parent)
        {
            var p = MakePanelHolder(parent, "PanelCreate");

            AddLabel(p, 0f, "Votre code de room :", 16, TXT_LIGHT, TextAnchor.MiddleCenter);

            var codeObj = new GameObject("Code");
            codeObj.transform.SetParent(p.transform, false);
            var codeRt = codeObj.AddComponent<RectTransform>();
            codeRt.anchorMin = new Vector2(0f, 1f);
            codeRt.anchorMax = new Vector2(1f, 1f);
            codeRt.pivot = new Vector2(0.5f, 1f);
            codeRt.anchoredPosition = new Vector2(0f, -34f);
            codeRt.sizeDelta = new Vector2(-20f, 90f);
            _createRoomCodeText = AddText(codeObj, "------", 56, TXT_ACCENT, TextAnchor.MiddleCenter);

            AddLabel(p, 140f, "Partagez ce code avec votre ami. Il pourra vous rejoindre depuis le menu Invité.",
                     14, TXT_DIM, TextAnchor.MiddleCenter);

            AddCustomButton(p, 200f, "Copier le code", () =>
            {
                if (!string.IsNullOrEmpty(_roomCode))
                {
                    GUIUtility.systemCopyBuffer = _roomCode;
                    SetStatus(_createStatusText, "Code copié dans le presse-papiers !", TXT_OK);
                }
            });

            AddCustomButton(p, 260f, "Fermer  (serveur actif en arrière-plan)", () => ShowState(MenuState.Closed));
            AddCustomButton(p, 320f, "Annuler et fermer la session", CancelSession);

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
            var jciTxt = _joinCodeInput.textComponent;
            if (jciTxt != null) { jciTxt.fontSize = 32; jciTxt.alignment = TextAnchor.MiddleCenter; }

            _joinSubmit = AddCustomButton(p, 120f, "Rejoindre", JoinRoom);
            _joinSubmit.Interactable = false;

            AddCustomButton(p, 190f, "Retour", () => ShowState(MenuState.Main));
            _joinStatusText = AddLabel(p, 260f, "", 14, TXT_DIM, TextAnchor.MiddleCenter);
            return p;
        }

        private GameObject BuildPanelConnected(GameObject parent)
        {
            var p = MakePanelHolder(parent, "PanelConnected");
            _connectedInfoText = AddLabel(p, 0f, "", 16, TXT_LIGHT, TextAnchor.MiddleLeft);
            var rt = _connectedInfoText.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, 110f);

            AddCustomButton(p, 140f, "Fermer le menu", () => ShowState(MenuState.Closed));
            AddCustomButton(p, 210f, "Quitter la session co-op", () =>
            {
                CoopNetworkManager.Instance?.StopCoop();
                _roomCode = "";
                ShowState(MenuState.Main);
                SetStatus(_createStatusText, "Session terminée.", TXT_DIM);
            });
            return p;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers de construction
        // ─────────────────────────────────────────────────────────────────────
        private GameObject MakePanelHolder(GameObject parent, string name)
        {
            var p = new GameObject(name);
            p.transform.SetParent(parent.transform, false);
            var rt = p.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.offsetMin = new Vector2(28f, 28f);
            rt.offsetMax = new Vector2(-28f, -58f);
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

            var textObj = new GameObject("Text");
            textObj.transform.SetParent(go.transform, false);
            var textRt = textObj.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero; textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(10f, 4f);
            textRt.offsetMax = new Vector2(-10f, -4f);
            var text = AddText(textObj, "", 18, TXT_LIGHT, TextAnchor.MiddleLeft);
            text.supportRichText = false;

            var phObj = new GameObject("Placeholder");
            phObj.transform.SetParent(go.transform, false);
            var phRt = phObj.AddComponent<RectTransform>();
            phRt.anchorMin = Vector2.zero; phRt.anchorMax = Vector2.one;
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

        /// <summary>Bouton sans Button/UnityAction — détection manuelle dans Update().</summary>
        private CustomButton AddCustomButton(GameObject parent, float yFromTop, string label, Action onClick)
        {
            var go = new GameObject("Btn_" + label);
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -yFromTop);
            rt.sizeDelta = new Vector2(0f, 56f);

            var img = go.AddComponent<Image>();
            img.color = BTN_NORMAL;
            img.raycastTarget = false; // pas besoin de raycaster, on détecte à la main

            var labelObj = new GameObject("Label");
            labelObj.transform.SetParent(go.transform, false);
            var lblRt = labelObj.AddComponent<RectTransform>();
            lblRt.anchorMin = Vector2.zero; lblRt.anchorMax = Vector2.one;
            lblRt.offsetMin = Vector2.zero; lblRt.offsetMax = Vector2.zero;
            var lblTxt = AddText(labelObj, label, 18, TXT_LIGHT, TextAnchor.MiddleCenter);
            lblTxt.fontStyle = FontStyle.Bold;

            var btn = new CustomButton
            {
                Rect    = rt,
                Bg      = img,
                OnClick = onClick,
                Owner   = parent
            };
            _buttons.Add(btn);
            return btn;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Gestion d'état
        // ─────────────────────────────────────────────────────────────────────
        private void ShowState(MenuState newState)
        {
            _state = newState;

            if (_canvasRoot != null) _canvasRoot.SetActive(true);

            bool open = newState != MenuState.Closed;
            if (_canvasRoot != null)
            {
                for (int i = 0; i < _canvasRoot.transform.childCount; i++)
                {
                    var child = _canvasRoot.transform.GetChild(i).gameObject;
                    if (child.name == "Overlay" || child.name == "Window")
                        child.SetActive(open);
                }
            }

            if (_panelMain != null)      _panelMain.SetActive(newState == MenuState.Main);
            if (_panelCreate != null)    _panelCreate.SetActive(newState == MenuState.CreateRoom);
            if (_panelJoin != null)      _panelJoin.SetActive(newState == MenuState.JoinRoom);
            if (_panelConnected != null) _panelConnected.SetActive(newState == MenuState.Connected);

            if (newState == MenuState.Connected) RefreshConnectedInfo();
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
            if (nm == null) { _connectedInfoText.text = "(pas de gestionnaire réseau)"; return; }

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
            if (_joinSubmit != null) _joinSubmit.Interactable = false;
            SetStatus(_joinStatusText, $"Résolution du code {code}…", TXT_DIM);

            GetOrAddRoomManager().ResolveRoom(code, (ip, port, err) =>
            {
                _isConnecting = false;
                if (!string.IsNullOrEmpty(err))
                {
                    SetStatus(_joinStatusText, $"Erreur : {err}", TXT_ERR);
                    if (_joinSubmit != null) _joinSubmit.Interactable = true;
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
            if (_playerNameInput != null && !string.IsNullOrEmpty(_playerNameInput.text))
                _playerName = _playerNameInput.text;
            if (!string.IsNullOrWhiteSpace(_playerName) && MiSideCoopPlugin.LocalPlayerName != null)
                MiSideCoopPlugin.LocalPlayerName.Value = _playerName;
        }

        private RoomManager GetOrAddRoomManager()
            => RoomManager.Instance ?? gameObject.AddComponent<RoomManager>();

        // ─────────────────────────────────────────────────────────────────────
        // (TryCreateOSFont supprimé : CreateDynamicFontFromOSFont est strippé en
        //  IL2CPP. La font est désormais récupérée via TryFindFontInScene en
        //  parcourant les Text components existants du jeu.)
        // ─────────────────────────────────────────────────────────────────────
    }
}

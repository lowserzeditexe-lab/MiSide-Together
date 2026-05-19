using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Attributes;
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

        // ── Couleurs — palette "Mita" (purple/magenta sur très sombre) ────────
        private static readonly Color BG_OVERLAY   = new Color(0f, 0f, 0f, 0.78f);
        private static readonly Color PANEL_BG     = new Color(0.078f, 0.063f, 0.102f, 0.97f); // #14101A
        private static readonly Color PANEL_BORDER = new Color(0.165f, 0.122f, 0.220f, 1f);   // #2A1F38
        private static readonly Color BTN_NORMAL   = new Color(0.129f, 0.102f, 0.176f, 1f);   // #211A2D
        private static readonly Color BTN_HOVER    = new Color(0.196f, 0.153f, 0.278f, 1f);   // #322747
        private static readonly Color BTN_PRESSED  = new Color(0.090f, 0.059f, 0.133f, 1f);   // #170F22
        private static readonly Color BTN_DISABLED = new Color(0.102f, 0.086f, 0.133f, 0.75f);
        private static readonly Color INPUT_BG     = new Color(0.055f, 0.039f, 0.078f, 1f);   // #0E0A14
        private static readonly Color TXT_LIGHT    = new Color(0.949f, 0.922f, 1f, 1f);       // #F2EBFF
        private static readonly Color TXT_DIM      = new Color(0.541f, 0.482f, 0.659f, 1f);   // #8A7BA8
        private static readonly Color TXT_ACCENT   = new Color(1f, 0.435f, 0.659f, 1f);       // #FF6FA8 (Mita pink)
        private static readonly Color TXT_OK       = new Color(0.424f, 0.902f, 0.608f, 1f);   // #6CE69B
        private static readonly Color TXT_ERR      = new Color(1f, 0.435f, 0.435f, 1f);       // #FF6F6F

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
                MiSideCoopPlugin.Logger?.LogInfo("[Co-op] UI built. Searching for game font…");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogError($"[Co-op] UI build failed: {ex}");
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
            // 0. Recherche de font : on continue jusqu'à 30 tentatives, MÊME si on
            //    a déjà Arial — l'objectif est de trouver la font custom de MiSide
            //    qui n'apparaît dans la scène qu'après chargement complet de l'UI
            //    du jeu. Une fois trouvée, on upgrade _font et on re-applique.
            if (_fontSearchTries < 30)
            {
                _fontSearchTries++;
                Font found = null;
                try
                {
                    found = TryFindFontInScene();
                }
                catch (Exception ex)
                {
                    _fontSearchTries = 999;
                    MiSideCoopPlugin.Logger?.LogWarning($"[Co-op] Font search aborted (IL2CPP): {ex.Message}");
                }

                if (found != null)
                {
                    // Décider si c'est un upgrade : _font null → toujours.
                    // _font déjà set en Arial-ish + found non-Arial-ish → upgrade.
                    bool isUpgrade =
                        _font == null ||
                        (IsArialishName(_font.name) && !IsArialishName(found.name));

                    if (isUpgrade)
                    {
                        var oldName = _font?.name;
                        _font = found;
                        _fontApplied = false; // force re-application
                        ApplyFontEverywhere();
                        if (oldName == null)
                            MiSideCoopPlugin.Logger?.LogInfo($"[Co-op] Font found in scene: {_font.name}");
                        else
                            MiSideCoopPlugin.Logger?.LogInfo($"[Co-op] Font upgraded: {oldName} → {_font.name}");

                        // Si on a maintenant une font non-Arial, on arrête la recherche.
                        if (!IsArialishName(_font.name))
                            _fontSearchTries = 999;
                    }
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
        ///   • MiSide / Unity 2021.3.35 → "Arial.ttf" (confirmé en runtime v1.1.1)
        ///   • Unity 2022.x+             → "LegacyRuntime.ttf" (renommage Arial)
        ///
        /// Ordre : Arial.ttf testé en PREMIER pour éviter l'erreur native Unity
        /// "could not be loaded from the resource file!" émise par le moteur natif
        /// (non capturable côté C# — émise via le canal log Unity directement)
        /// quand on demande LegacyRuntime.ttf sur un build qui ne l'expose pas.
        ///
        /// Voie B : scan de la hiérarchie sous <c>Camera.main.transform.root</c> pour
        /// récupérer une font déjà utilisée par le jeu (filet de sécurité ultime).
        /// </summary>
        private static Font TryFindFontInScene()
        {
            // ── v1.2.3 : tentative OS font Google Sans (et alternatives modernes) ──
            // Font.CreateDynamicFontFromOSFont peut être strippé dans certains
            // builds IL2CPP mais le try/catch capture le MissingMethodException
            // sans casser la suite. Si l'utilisateur a installé Google Sans sur
            // Windows (https://fonts.google.com/specimen/Google+Sans), elle est
            // récupérée ici. Sinon on cascade vers Product Sans, Segoe UI, etc.
            // Si l'API est strippée → tout échoue silencieusement → fallback Arial.
            string[] preferredOsFonts = {
                "Google Sans", "Product Sans", "Roboto",
                "Segoe UI Variable", "Segoe UI", "Calibri"
            };
            foreach (var name in preferredOsFonts)
            {
                var osF = SafeCreateOSFont(name);
                if (osF != null) return osF;
            }

            // ── v1.2.5 : scan scène EN PRIORITÉ pour la font custom MiSide ──
            // MiSide charge sa propre font custom (la pourpre/rose chunky du logo,
            // visible dans le menu principal). Elle est attachée aux Text components
            // de l'UI du jeu, accessible via GetComponent<Text>().font.
            // Stratégie : on scanne le hierarchy de Camera.main et on RETOURNE la
            // 1ère font dont le nom ne contient PAS "Arial" / "Legacy" → c'est
            // forcément la custom font MiSide. Si rien d'autre trouvé, on accepte
            // l'Arial trouvée en fallback.
            var camRoot = SafeGetMainCameraRoot();
            if (camRoot != null)
            {
                Font arialFallback = null;
                var custom = ScanTransformForCustomFont(camRoot, 0, ref arialFallback);
                if (custom != null) return custom;
                if (arialFallback != null) return arialFallback;
            }

            // ── Fallback ultime : built-in Unity ──
            var f = SafeGetBuiltinFont("Arial.ttf");
            if (f != null) return f;
            f = SafeGetBuiltinFont("LegacyRuntime.ttf");
            return f;
        }

        /// <summary>
        /// Charge une OS font via Font.CreateDynamicFontFromOSFont.
        ///
        /// IL2CPP : cette API peut être strippée selon le build. Le try/catch
        /// silencieux + flag sticky empêche le spam de logs et permet de
        /// cascader vers les fonts suivantes / built-in Arial.
        ///
        /// Note : sur Windows, si la font demandée n'est pas installée, Unity
        /// retourne quand même un Font (fallback système). On vérifie que le
        /// name retourné contient au moins une partie du nom demandé pour
        /// éviter d'accepter un faux positif "Arial déguisé".
        /// </summary>
        private static bool _osFontApiBroken; // sticky : on n'essaie qu'une fois
        private static Font SafeCreateOSFont(string osName)
        {
            if (_osFontApiBroken) return null;
            try
            {
                var f = Font.CreateDynamicFontFromOSFont(osName, 16);
                if (f == null) return null;
                // Heuristique : on accepte la font si son nom contient le 1er mot
                // demandé (ex: "Google Sans" matche "Google Sans Medium").
                // Sinon Unity a renvoyé un fallback système → on rejette pour
                // cascader vers la prochaine OS font.
                var firstWord = osName.Split(' ')[0];
                if (!string.IsNullOrEmpty(f.name) &&
                    f.name.IndexOf(firstWord, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return f;
                }
                return null;
            }
            catch (Exception ex)
            {
                // 1ère exception → on log puis on désactive la cascade entière
                // pour ne pas multiplier les exceptions identiques.
                _osFontApiBroken = true;
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] OS font API unavailable in this IL2CPP build " +
                    $"({ex.GetType().Name}: {ex.Message}). Falling back to built-in Arial.");
                return null;
            }
        }

        /// <summary>Charge une police intégrée Unity, en silence si l'API est strippée.</summary>
        private static Font SafeGetBuiltinFont(string fontName)
        {
            try { return Resources.GetBuiltinResource<Font>(fontName); }
            catch { return null; }
        }

        /// <summary>
        /// Détecte les fonts "génériques fallback" (Arial Unity built-in, LegacyRuntime)
        /// qu'on veut remplacer dès qu'une font custom MiSide est disponible.
        /// </summary>
        private static bool IsArialishName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            return name.IndexOf("Arial", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Legacy", StringComparison.OrdinalIgnoreCase) >= 0;
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

        /// <summary>
        /// Récursion limitée en profondeur sur les enfants à la recherche d'un Text avec font.
        ///
        /// NB : on utilise la version GÉNÉRIQUE <c>GetComponent&lt;Text&gt;()</c>.
        /// La surcharge non-générique <c>GetComponent(System.Type)</c> est STRIPPÉE
        /// dans ce build MiSide (MissingMethodException constatée en v1.1.1).
        /// La générique passe par l'interop IL2CPP et reste disponible.
        /// </summary>
        private static Font ScanTransformForFont(Transform t, int depth)
        {
            if (t == null || depth > 8) return null;

            try
            {
                var txtComp = t.GetComponent<Text>();
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

        /// <summary>
        /// v1.2.5 : variante du scan qui PRÉFÈRE une font non-Arial.
        ///
        /// On parcourt récursivement le hierarchy à la recherche de Text components,
        /// et on RETOURNE la 1ère font dont le nom ne contient ni "Arial" ni
        /// "Legacy" — c'est très probablement la font custom embedded de MiSide
        /// (visible dans le logo et les boutons du menu principal).
        ///
        /// Les fonts Arial rencontrées sont stockées dans <paramref name="arialFallback"/>
        /// au cas où aucune custom font ne serait trouvée → l'appelant pourra
        /// l'utiliser à la place de built-in Arial (mêmes propriétés mais ça évite
        /// un appel supplémentaire à Resources.GetBuiltinResource).
        /// </summary>
        private static Font ScanTransformForCustomFont(Transform t, int depth, ref Font arialFallback)
        {
            if (t == null || depth > 10) return null;

            try
            {
                var txtComp = t.GetComponent<Text>();
                if (txtComp != null && txtComp.font != null)
                {
                    var name = txtComp.font.name ?? "";
                    bool isArialish =
                        name.IndexOf("Arial", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Legacy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        string.IsNullOrEmpty(name);

                    if (!isArialish)
                        return txtComp.font; // custom MiSide font !

                    if (arialFallback == null)
                        arialFallback = txtComp.font;
                }
            }
            catch { /* GetComponent IL2CPP shenanigans — on continue */ }

            int count;
            try { count = t.childCount; } catch { return null; }
            for (int i = 0; i < count; i++)
            {
                Transform child;
                try { child = t.GetChild(i); } catch { continue; }
                var f = ScanTransformForCustomFont(child, depth + 1, ref arialFallback);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>Applique _font à tous nos Text déjà créés (rétroactif).</summary>
        private void ApplyFontEverywhere()
        {
            if (_canvasRoot == null || _font == null) return;

            // IMPORTANT : on marque _fontApplied AVANT d'essayer pour empêcher tout
            // re-déclenchement frame-par-frame en cas d'exception (le check de
            // Update() utilise !_fontApplied comme garde — sans ça l'erreur
            // "Method not found GetComponent(System.Type)" était spammée à 60 Hz).
            _fontApplied = true;
            try
            {
                ApplyFontRecursive(_canvasRoot.transform);
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning($"[Co-op] ApplyFontEverywhere: {ex.Message}");
            }
        }

        private void ApplyFontRecursive(Transform t)
        {
            if (t == null) return;
            // Générique uniquement (non-générique strippé dans MiSide — cf. ScanTransformForFont)
            var txtComp = t.GetComponent<Text>();
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
                        MiSideCoopPlugin.Logger?.LogError($"[Co-op] Button OnClick crashed: {ex}");
                    }
                    // ⚠️ break OBLIGATOIRE : sans ça, le même mouseDown propage la
                    // cascade aux boutons des autres panels qui viennent d'être
                    // activés par ShowState() dans le OnClick. Bug constaté v1.2.0
                    // où cliquer "CREATE A ROOM" déclenchait simultanément
                    // "COPY CODE" (panel Create venait juste d'apparaître au même
                    // endroit écran).
                    return;
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

            // GraphicRaycaster : indispensable pour que l'Overlay BLOQUE les clics
            // vers le menu du jeu en-dessous. Sans ça, un clic sur "CREATE A ROOM"
            // est aussi reçu par le bouton "Options" de MiSide qui se trouve à la
            // même position écran → bug v1.2.0 où le menu disparaissait après clic
            // (le jeu déclenchait une transition de fond qui masquait notre canvas).
            _canvasRoot.AddComponent<GraphicRaycaster>();

            // Overlay sombre plein écran (raycastTarget=true par défaut → absorbe les clics)
            var overlay = CreateImage(_canvasRoot, "Overlay", BG_OVERLAY);
            StretchToParent(overlay.GetComponent<RectTransform>());
            overlay.raycastTarget = true;

            // HUD discret en haut-gauche (pill arrondie avec fond sombre)
            _hudRoot = new GameObject("HUD");
            _hudRoot.transform.SetParent(_canvasRoot.transform, false);
            var hudRt = _hudRoot.AddComponent<RectTransform>();
            hudRt.anchorMin = hudRt.anchorMax = new Vector2(0f, 1f);
            hudRt.pivot     = new Vector2(0f, 1f);
            hudRt.anchoredPosition = new Vector2(20f, -20f);
            hudRt.sizeDelta = new Vector2(560f, 32f);
            var hudBg = _hudRoot.AddComponent<Image>();
            hudBg.color = new Color(0f, 0f, 0f, 0.55f);
            hudBg.raycastTarget = false;
            ApplyRounded(hudBg);

            var hudTxtObj = new GameObject("HUDText");
            hudTxtObj.transform.SetParent(_hudRoot.transform, false);
            var hudTxtRt = hudTxtObj.AddComponent<RectTransform>();
            hudTxtRt.anchorMin = Vector2.zero; hudTxtRt.anchorMax = Vector2.one;
            hudTxtRt.offsetMin = new Vector2(14f, 0f);
            hudTxtRt.offsetMax = new Vector2(-14f, 0f);
            _hudText = AddText(hudTxtObj, "", 15, TXT_LIGHT, TextAnchor.MiddleLeft);
            _hudRoot.SetActive(false);

            // Fenêtre centrale
            var window = new GameObject("Window");
            window.transform.SetParent(_canvasRoot.transform, false);
            var winRt = window.AddComponent<RectTransform>();
            winRt.anchorMin = winRt.anchorMax = new Vector2(0.5f, 0.5f);
            winRt.pivot = new Vector2(0.5f, 0.5f);
            winRt.sizeDelta = new Vector2(680f, 600f);
            var winImg = window.AddComponent<Image>();
            winImg.color = PANEL_BG;
            ApplyRounded(winImg);

            var border = CreateImage(window, "Border", PANEL_BORDER);
            var borderRt = border.GetComponent<RectTransform>();
            borderRt.anchorMin = Vector2.zero; borderRt.anchorMax = Vector2.one;
            borderRt.offsetMin = new Vector2(-2f, -2f);
            borderRt.offsetMax = new Vector2(2f, 2f);
            border.transform.SetAsFirstSibling();
            ApplyRounded(border);

            // Bandeau d'accent en haut (barre pink fine sous le titre)
            var accent = CreateImage(window, "AccentBar", TXT_ACCENT);
            var accentRt = accent.GetComponent<RectTransform>();
            accentRt.anchorMin = new Vector2(0f, 1f);
            accentRt.anchorMax = new Vector2(1f, 1f);
            accentRt.pivot     = new Vector2(0.5f, 1f);
            accentRt.anchoredPosition = new Vector2(0f, -78f);
            accentRt.sizeDelta = new Vector2(-120f, 2f);

            // Titre
            var titleObj = new GameObject("Title");
            titleObj.transform.SetParent(window.transform, false);
            var titleRt = titleObj.AddComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, -22f);
            titleRt.sizeDelta = new Vector2(-40f, 32f);
            var titleTxt = AddText(titleObj, "MiSide Together", 28, TXT_ACCENT, TextAnchor.MiddleCenter);
            titleTxt.fontStyle = FontStyle.Bold;

            // Sous-titre
            var subtitleObj = new GameObject("Subtitle");
            subtitleObj.transform.SetParent(window.transform, false);
            var subRt = subtitleObj.AddComponent<RectTransform>();
            subRt.anchorMin = new Vector2(0f, 1f);
            subRt.anchorMax = new Vector2(1f, 1f);
            subRt.pivot = new Vector2(0.5f, 1f);
            subRt.anchoredPosition = new Vector2(0f, -54f);
            subRt.sizeDelta = new Vector2(-40f, 20f);
            AddText(subtitleObj, "CO-OP MULTIPLAYER MOD", 12, TXT_DIM, TextAnchor.MiddleCenter);

            // Footer version (coin bas droit de la fenêtre)
            var footerObj = new GameObject("Footer");
            footerObj.transform.SetParent(window.transform, false);
            var footRt = footerObj.AddComponent<RectTransform>();
            footRt.anchorMin = new Vector2(0f, 0f);
            footRt.anchorMax = new Vector2(1f, 0f);
            footRt.pivot = new Vector2(0.5f, 0f);
            footRt.anchoredPosition = new Vector2(0f, 10f);
            footRt.sizeDelta = new Vector2(-20f, 18f);
            AddText(footerObj, $"v{PluginInfo.PLUGIN_VERSION}  •  Press F8 to toggle this menu",
                    11, TXT_DIM, TextAnchor.MiddleCenter);

            _panelMain      = BuildPanelMain(window);
            _panelCreate    = BuildPanelCreate(window);
            _panelJoin      = BuildPanelJoin(window);
            _panelConnected = BuildPanelConnected(window);

            // ─── EventSystem deduplication ─────────────────────────────────────
            // L'ajout d'un Canvas + InputField peut entraîner la création
            // automatique d'un EventSystem par Unity (StandaloneInputModule).
            // MiSide en a déjà un actif → Unity émet le warning
            //   "There can be only one active Event System."
            // On scanne notre hiérarchie pour désactiver tout EventSystem
            // qu'on aurait inadvertamment créé. L'EventSystem du jeu reste
            // l'unique actif et continue de gérer les inputs de nos InputField.
            DisableEventSystemsInHierarchy(_canvasRoot.transform);
        }

        /// <summary>
        /// Désactive tout <see cref="UnityEngine.EventSystems.EventSystem"/>
        /// trouvé sous le transform fourni. Récursif, profondeur limitée à 6.
        /// </summary>
        private static void DisableEventSystemsInHierarchy(Transform root)
        {
            DisableEventSystemRecursive(root, 0);
        }

        private static void DisableEventSystemRecursive(Transform t, int depth)
        {
            if (t == null || depth > 6) return;
            try
            {
                // Générique (IL2CPP-safe — la surcharge typeof() est strippée
                // dans MiSide, cf. corrections font search v1.1.2).
                var es = t.GetComponent<UnityEngine.EventSystems.EventSystem>();
                if (es != null)
                {
                    es.enabled = false;
                    MiSideCoopPlugin.Logger?.LogInfo(
                        "[Co-op] Duplicate EventSystem detected in our hierarchy — disabled (game keeps its own).");
                }
            }
            catch { /* GetComponent peut bouger en IL2CPP — on continue */ }

            for (int i = 0; i < t.childCount; i++)
                DisableEventSystemRecursive(t.GetChild(i), depth + 1);
        }

        private GameObject BuildPanelMain(GameObject parent)
        {
            var p = MakePanelHolder(parent, "PanelMain");

            AddLabel(p,  0f, "Play MiSide with a friend in the same session.", 16, TXT_DIM, TextAnchor.MiddleCenter);
            AddLabel(p, 28f, "Press F8 anytime to close or reopen this menu.",  13, TXT_DIM, TextAnchor.MiddleCenter);

            AddLabel(p, 80f, "Your nickname", 14, TXT_LIGHT, TextAnchor.MiddleLeft);
            _playerNameInput = AddInputField(p, 110f, _playerName, 24, "Enter your nickname…");

            AddCustomButton(p, 180f, "CREATE A ROOM  ·  HOST", () =>
            {
                SavePlayerName();
                ShowState(MenuState.CreateRoom);
                CreateRoom();
            });

            AddCustomButton(p, 252f, "JOIN A ROOM  ·  GUEST", () =>
            {
                SavePlayerName();
                ShowState(MenuState.JoinRoom);
            });

            return p;
        }

        private GameObject BuildPanelCreate(GameObject parent)
        {
            var p = MakePanelHolder(parent, "PanelCreate");

            AddLabel(p, 0f, "YOUR ROOM CODE", 13, TXT_DIM, TextAnchor.MiddleCenter);

            var codeObj = new GameObject("Code");
            codeObj.transform.SetParent(p.transform, false);
            var codeRt = codeObj.AddComponent<RectTransform>();
            codeRt.anchorMin = new Vector2(0f, 1f);
            codeRt.anchorMax = new Vector2(1f, 1f);
            codeRt.pivot = new Vector2(0.5f, 1f);
            codeRt.anchoredPosition = new Vector2(0f, -28f);
            codeRt.sizeDelta = new Vector2(-20f, 96f);
            _createRoomCodeText = AddText(codeObj, "------", 64, TXT_ACCENT, TextAnchor.MiddleCenter);
            _createRoomCodeText.fontStyle = FontStyle.Bold;

            AddLabel(p, 140f, "Share this code with your friend — they can join from the Guest menu.",
                     13, TXT_DIM, TextAnchor.MiddleCenter);

            AddCustomButton(p, 200f, "COPY CODE", () =>
            {
                if (!string.IsNullOrEmpty(_roomCode))
                {
                    GUIUtility.systemCopyBuffer = _roomCode;
                    SetStatus(_createStatusText, "Code copied to clipboard!", TXT_OK);
                }
            });

            AddCustomButton(p, 268f, "CLOSE MENU  ·  KEEP SERVER RUNNING", () => ShowState(MenuState.Closed));
            AddCustomButton(p, 336f, "CANCEL AND CLOSE SESSION", CancelSession);

            _createStatusText = AddLabel(p, 400f, "", 13, TXT_DIM, TextAnchor.MiddleCenter);
            return p;
        }

        private GameObject BuildPanelJoin(GameObject parent)
        {
            var p = MakePanelHolder(parent, "PanelJoin");

            AddLabel(p, 0f, "Enter the host's 6-character room code",
                     14, TXT_LIGHT, TextAnchor.MiddleCenter);

            _joinCodeInput = AddInputField(p, 40f, "", 6, "ABC123");
            _joinCodeInput.characterLimit = 6;
            var jciTxt = _joinCodeInput.textComponent;
            if (jciTxt != null) { jciTxt.fontSize = 36; jciTxt.alignment = TextAnchor.MiddleCenter; }

            _joinSubmit = AddCustomButton(p, 130f, "JOIN ROOM", JoinRoom);
            _joinSubmit.Interactable = false;

            AddCustomButton(p, 200f, "BACK", () => ShowState(MenuState.Main));
            _joinStatusText = AddLabel(p, 280f, "", 13, TXT_DIM, TextAnchor.MiddleCenter);
            return p;
        }

        private GameObject BuildPanelConnected(GameObject parent)
        {
            var p = MakePanelHolder(parent, "PanelConnected");
            _connectedInfoText = AddLabel(p, 0f, "", 15, TXT_LIGHT, TextAnchor.MiddleLeft);
            var rt = _connectedInfoText.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, 130f);

            AddCustomButton(p, 160f, "CLOSE MENU", () => ShowState(MenuState.Closed));
            AddCustomButton(p, 228f, "LEAVE CO-OP SESSION", () =>
            {
                CoopNetworkManager.Instance?.StopCoop();
                _roomCode = "";
                ShowState(MenuState.Main);
                SetStatus(_createStatusText, "Session ended.", TXT_DIM);
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
            // Marges : 40px bas (footer version) / 100px haut (titre + sous-titre + accent bar) / 36px latéral.
            rt.offsetMin = new Vector2(36f, 40f);
            rt.offsetMax = new Vector2(-36f, -100f);
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

        // ── Sprite rounded-rect partagé (IL2CPP-safe) ────────────────────────
        //
        // Génère une texture 64×64 avec coins arrondis (rayon 16px) et
        // antialiasing 1-px, puis crée un Sprite 9-slice via Sprite.Create
        // avec border = (radius, radius, radius, radius). Une fois assigné à
        // un Image (type=Sliced), le sprite s'étire sans déformer les coins.
        //
        // Texture2D/SetPixels32/Sprite.Create sont massivement utilisés par
        // MiSide en interne → confirmés non-strippés. try/catch garde une
        // sortie propre vers les coins droits si jamais le runtime change.
        private static Sprite _roundedSprite;
        private static bool   _roundedSpriteFailed; // sticky : on n'essaie qu'une fois

        // ─────────────────────────────────────────────────────────────────────
        // CONTEXTE DU FIX v1.2.2 — MissingMethodException SetPixels32(Color32[])
        // ─────────────────────────────────────────────────────────────────────
        // Sur certains builds IL2CPP de MiSide, le proxy Il2CppInterop de
        // Texture2D expose SetPixels32 avec la signature Il2CppStructArray<Color32>
        // au lieu de Color32[]. Le compile-time reference (UnityEngine.Modules
        // NuGet) résout Color32[] → le JIT runtime ne trouve pas la méthode et
        // lève MissingMethodException AVANT que le corps de la méthode appelée
        // s'exécute. Conséquence : le try/catch INTERNE à GetRoundedSprite n'a
        // jamais sa "protected region" établie. L'exception remonte au frame
        // appelant. C'est pourquoi on isole SetPixels32 dans TryApplyPixels32
        // (le JIT-fail est confiné dans son frame, le try ENGLOBANT de
        // GetRoundedSprite est dans un autre frame → il catch correctement).
        // On ajoute aussi un filet de sécurité dans ApplyRounded.
        // ─────────────────────────────────────────────────────────────────────
        private static Sprite GetRoundedSprite()
        {
            if (_roundedSpriteFailed) return null;
            if (_roundedSprite != null) return _roundedSprite;
            try
            {
                const int size = 64;
                const int radius = 16;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;

                var pixels = new Color32[size * size];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        // Centre du cercle pour ce coin (le plus proche du pixel)
                        int cx = x < radius ? radius : (x >= size - radius ? size - radius - 1 : x);
                        int cy = y < radius ? radius : (y >= size - radius ? size - radius - 1 : y);
                        float dx = x - cx, dy = y - cy;
                        float dist = Mathf.Sqrt(dx * dx + dy * dy);
                        float a;
                        if (dist <= radius - 1f) a = 1f;
                        else if (dist >= radius)  a = 0f;
                        else                      a = radius - dist; // antialiased 1-px edge
                        pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                    }
                }

                // SetPixels32 isolé dans un helper séparé : si le JIT du helper
                // échoue (signature Color32[] absente côté IL2CPP), l'exception
                // remonte ICI et est attrapée par le catch ci-dessous. Si on
                // l'appelait inline, le JIT-fail s'appliquerait à GetRoundedSprite
                // elle-même et le try block ne serait jamais établi.
                if (!TryApplyPixels32(tex, pixels))
                {
                    _roundedSpriteFailed = true;
                    return null;
                }
                tex.Apply(false, true);

                _roundedSprite = Sprite.Create(
                    tex,
                    new Rect(0, 0, size, size),
                    new Vector2(0.5f, 0.5f),
                    100f, 0,
                    SpriteMeshType.FullRect,
                    new Vector4(radius, radius, radius, radius));
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] Rounded sprite gen failed (IL2CPP {ex.GetType().Name}): {ex.Message}. Falling back to square corners.");
                _roundedSprite = null;
                _roundedSpriteFailed = true;
            }
            return _roundedSprite;
        }

        /// <summary>
        /// Remplit une Texture2D pixel-par-pixel via SetPixel(int, int, Color).
        ///
        /// FIX v1.2.3 : SetPixels32(Color32[]) est cassé en IL2CPP MiSide
        /// (signature managée Color32[] absente du proxy Il2CppInterop, qui
        /// expose Il2CppStructArray&lt;Color32&gt;). On contourne en utilisant
        /// SetPixel qui ne prend QUE des primitives (int, int) + struct Color
        /// (4 floats) — aucun array à marshaller, donc IL2CPP-safe.
        ///
        /// Coût : 4096 itérations pour une 64×64. Insignifiant (1× au boot).
        ///
        /// Isolé dans son propre frame pour que le JIT-fail éventuel reste
        /// confiné ici et soit attrapé par le try englobant de GetRoundedSprite.
        /// </summary>
        private static bool TryApplyPixels32(Texture2D tex, Color32[] pixels)
        {
            int size = (int)Mathf.Sqrt(pixels.Length);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var c = pixels[y * size + x];
                    tex.SetPixel(x, y, new Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f));
                }
            }
            return true;
        }

        /// <summary>Active les coins arrondis sur une Image (no-op si génération a échoué).</summary>
        private static void ApplyRounded(Image img)
        {
            // Court-circuit immédiat si la première tentative a échoué.
            if (_roundedSpriteFailed) return;
            try
            {
                var sp = GetRoundedSprite();
                if (sp == null) return;
                img.sprite = sp;
                img.type = Image.Type.Sliced;
                img.pixelsPerUnitMultiplier = 1f;
            }
            catch (Exception ex)
            {
                // Filet de sécurité ultime : on log UNE fois et on désactive
                // définitivement la décoration arrondie pour ne pas casser la
                // construction de l'UI sur les builds IL2CPP capricieux.
                if (!_roundedSpriteFailed)
                {
                    _roundedSpriteFailed = true;
                    MiSideCoopPlugin.Logger?.LogWarning(
                        $"[Co-op] ApplyRounded disabled (IL2CPP {ex.GetType().Name}): {ex.Message}");
                }
            }
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
            ApplyRounded(bg);

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
        [HideFromIl2Cpp]
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
            // raycastTarget=true → l'overlay/canvas absorbe les clics avant qu'ils
            // n'atteignent le menu du jeu en-dessous (la détection de clic mod
            // reste manuelle via Input.GetMouseButtonDown).
            img.raycastTarget = true;
            ApplyRounded(img);

            var labelObj = new GameObject("Label");
            labelObj.transform.SetParent(go.transform, false);
            var lblRt = labelObj.AddComponent<RectTransform>();
            lblRt.anchorMin = Vector2.zero; lblRt.anchorMax = Vector2.one;
            lblRt.offsetMin = Vector2.zero; lblRt.offsetMax = Vector2.zero;
            var lblTxt = AddText(labelObj, label, 17, TXT_LIGHT, TextAnchor.MiddleCenter);
            // Style buttons : weight Normal + letter-spacing visuel (espaces) pour
            // matcher l'esthétique du menu MiSide natif (regular all-caps).
            lblTxt.fontStyle = FontStyle.Normal;

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
            if (nm == null) { _connectedInfoText.text = "(network manager unavailable)"; return; }

            string role = nm.IsHost ? "Host" : "Guest";
            string remote = string.IsNullOrEmpty(nm.ConnectedPlayerName) ? "..." : nm.ConnectedPlayerName;
            string code = string.IsNullOrEmpty(nm.RoomCode) ? "(none)" : nm.RoomCode;
            string ping = _pingMs >= 0 ? $"{_pingMs} ms" : "N/A";

            _connectedInfoText.text =
                $"Co-op session active  —  Role: {role}\n" +
                $"Room code:  {code}\n" +
                $"Remote player:  {remote}\n" +
                $"Ping:  {ping}";
        }

        private void UpdateHud()
        {
            if (_hudRoot == null) return;
            var nm = CoopNetworkManager.Instance;
            bool show = _state == MenuState.Closed && nm != null && nm.IsConnected;
            _hudRoot.SetActive(show);
            if (!show) return;
            string role = nm.IsHost ? "Host" : "Guest";
            string ping = _pingMs >= 0 ? $"{_pingMs}ms" : "N/A";
            _hudText.text = $"[MiSide Together] {role}  |  {nm.ConnectedPlayerName}  |  {ping}  |  F8: menu";
        }

        // ─────────────────────────────────────────────────────────────────────
        // Logique métier
        // ─────────────────────────────────────────────────────────────────────
        private void CreateRoom()
        {
            _roomCode = "";
            if (_createRoomCodeText != null) _createRoomCodeText.text = "------";
            SetStatus(_createStatusText, "Creating room…", TXT_DIM);

            var relay = GetOrAddRoomManager();
            string code = relay.GenerateRoomCode();
            CoopNetworkManager.Instance?.StartHost(code);

            relay.RegisterRoom(code, ok =>
            {
                _roomCode = code;
                if (_createRoomCodeText != null) _createRoomCodeText.text = code;
                SetStatus(_createStatusText,
                    ok ? "Room created. Waiting for guest…"
                       : "Relay unavailable — direct IP mode active.",
                    ok ? TXT_OK : TXT_ERR);
            });
        }

        private void JoinRoom()
        {
            var code = (_joinCodeInput?.text ?? "").ToUpperInvariant();
            if (code.Length != 6) return;

            _isConnecting = true;
            if (_joinSubmit != null) _joinSubmit.Interactable = false;
            SetStatus(_joinStatusText, $"Resolving code {code}…", TXT_DIM);

            GetOrAddRoomManager().ResolveRoom(code, (ip, port, err) =>
            {
                _isConnecting = false;
                if (!string.IsNullOrEmpty(err))
                {
                    SetStatus(_joinStatusText, $"Error: {err}", TXT_ERR);
                    if (_joinSubmit != null) _joinSubmit.Interactable = true;
                    return;
                }
                SetStatus(_joinStatusText, $"Connecting to {ip}:{port}…", TXT_OK);
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
            SetStatus(_createStatusText, "Session cancelled.", TXT_DIM);
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

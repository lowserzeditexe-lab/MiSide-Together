using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using MiSideCoop.Avatars;

namespace MiSideCoop.UI
{
    /// <summary>
    /// v1.4.4 — Déclenche programmatiquement la "Nouvelle Partie" du menu MiSide,
    /// IL2CPP-safe.
    ///
    /// HISTORIQUE
    /// ──────────
    /// v1.4.3 — Walk depuis Camera.main + scan UnityEngine.UI.Button + match texte
    ///   sur libellés FR/EN/RU/ES. ÉCHEC en jeu : MiSide utilise TextMeshPro (TMP)
    ///   au lieu de Text legacy → ReadText() retourne null → aucun match.
    ///   En plus le Canvas du menu n'est pas forcément sous Camera.main.transform.root.
    ///
    /// v1.4.4 — STRATÉGIE DIRECTE :
    ///   Le dump Il2CppDumper révèle la classe MiSide `Menu` (TypeDefIndex 1583,
    ///   dump.cs:103851) qui expose une méthode publique `void ButtonNewGame()`
    ///   (dump.cs:103971). Le clic sur "NOUVELLE PARTIE" dans le menu principal
    ///   invoke cette méthode via UnityEvent. On peut donc l'appeler nous-mêmes
    ///   en récupérant l'instance de Menu dans la scène et en appelant la méthode
    ///   via System.Reflection — bypass complet du système UI.
    ///
    /// PATH FALLBACK :
    ///   Si la classe Menu n'est pas trouvée (version différente, modif IL2CPP),
    ///   on retombe sur l'ancienne stratégie Button + text matching (v1.4.3) en
    ///   incluant maintenant TMPro.TextMeshProUGUI via réflexion.
    /// </summary>
    internal static class MenuButtonClicker
    {
        // ── Réflexion résolue à l'init ────────────────────────────────────────
        private static bool _reflectionResolved;
        private static Type _menuType;                       // System.Type de la classe MiSide "Menu"
        private static MethodInfo _buttonNewGameMethod;      // Menu.ButtonNewGame()
        private static MethodInfo _il2cppTypeOfGenericDef;   // Il2CppType.Of<T>()
        private static MethodInfo _gameObjectGetComponentByIl2CppType;  // GameObject.GetComponent(Il2CppSystem.Type)

        private static void ResolveReflectionOnce()
        {
            if (_reflectionResolved) return;
            _reflectionResolved = true;

            try
            {
                // 1) Récupère la classe MiSide "Menu" via AccessTools (Harmony).
                //    AccessTools.TypeByName cherche dans TOUS les assemblies
                //    chargés (incluant Il2Cppmscorlib et les wrappers du jeu).
                _menuType = AccessTools.TypeByName("Menu");
                if (_menuType != null)
                {
                    _buttonNewGameMethod = _menuType.GetMethod(
                        "ButtonNewGame",
                        BindingFlags.Public | BindingFlags.Instance);
                    MiSideCoopPlugin.Logger?.LogInfo(
                        $"[Co-op] MenuButtonClicker: Menu type resolved ({_menuType.FullName}). "
                      + $"ButtonNewGame method = {(_buttonNewGameMethod != null ? "FOUND" : "missing")}.");
                }
                else
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        "[Co-op] MenuButtonClicker: 'Menu' MiSide class not found via AccessTools "
                      + "(will fall back to UI Button scan).");
                }

                // 2) Résout Il2CppType.Of<T>() pour pouvoir construire un Il2CppSystem.Type
                //    à partir d'un System.Type (path standard Il2CppInterop).
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "Il2CppInterop.Runtime") continue;
                    var cls = asm.GetType("Il2CppInterop.Runtime.Il2CppType");
                    if (cls == null) continue;
                    _il2cppTypeOfGenericDef = cls
                        .GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "Of"
                                          && m.IsGenericMethodDefinition
                                          && m.GetParameters().Length == 0);
                    break;
                }

                // 3) Résout GameObject.GetComponent(Il2CppSystem.Type) via probe.
                //    Pattern identique à Il2CppAddComponentHelper v1.3.6.
                if (_il2cppTypeOfGenericDef != null)
                {
                    object probe = null;
                    try
                    {
                        probe = _il2cppTypeOfGenericDef
                            .MakeGenericMethod(typeof(Camera))
                            .Invoke(null, null);
                    }
                    catch { }
                    if (probe != null)
                    {
                        var il2cppSystemTypeRuntime = probe.GetType();
                        _gameObjectGetComponentByIl2CppType = typeof(GameObject)
                            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .FirstOrDefault(m => m.Name == "GetComponent"
                                              && m.GetParameters().Length == 1
                                              && m.GetParameters()[0].ParameterType == il2cppSystemTypeRuntime);
                    }
                }

                if (_menuType != null && _buttonNewGameMethod != null
                    && _il2cppTypeOfGenericDef != null && _gameObjectGetComponentByIl2CppType != null)
                {
                    MiSideCoopPlugin.Logger?.LogInfo(
                        "[Co-op] MenuButtonClicker: full reflection wiring ready (direct Menu.ButtonNewGame call).");
                }
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] MenuButtonClicker.ResolveReflection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Cherche l'instance MiSide <c>Menu</c> active dans la scène et appelle
        /// directement sa méthode <c>ButtonNewGame()</c>. Retourne <c>true</c>
        /// si la méthode a été invoquée, <c>false</c> sinon (fallback UI tenté).
        /// </summary>
        public static bool ClickNewGame()
        {
            try
            {
                ResolveReflectionOnce();

                // ── PATH PRINCIPAL : invoke direct Menu.ButtonNewGame() ──────
                if (_menuType != null && _buttonNewGameMethod != null
                    && _il2cppTypeOfGenericDef != null && _gameObjectGetComponentByIl2CppType != null)
                {
                    var menuInstance = FindMenuInstance();
                    if (menuInstance != null)
                    {
                        string ownerName = "(unknown)";
                        try { ownerName = ((Component)menuInstance).gameObject.name; } catch { }

                        // v1.4.6 — 3 stratégies d'invocation en cascade.
                        // En IL2CPP MiSide, MethodInfo.Invoke échoue sur les méthodes
                        // de classes wrapped Il2CppInterop avec "Object does not match
                        // target type" (DeclaringType de la MethodInfo != runtime type
                        // de l'instance — mismatch wrapper / proxy).

                        // ── Stratégie A : Harmony MethodDelegate ──
                        // AccessTools.MethodDelegate<T> compile un IL stub typé via
                        // ILGenerator qui appelle directement la méthode en bypassant
                        // les checks d'IsInstanceOfType.
                        if (TryInvokeViaHarmonyDelegate(menuInstance, ownerName)) return true;

                        // ── Stratégie B : invoke UnityEvent eventStartNewGame ──
                        // Le dump expose un champ public UnityEvent eventStartNewGame
                        // (Menu.cs ligne 103884). UnityEvent.Invoke() est plus portable
                        // que MethodInfo.Invoke sur les types IL2CPP.
                        if (TryInvokeViaUnityEvent(menuInstance, ownerName)) return true;

                        // ── Stratégie C : runtime-type MethodInfo.Invoke (v1.4.5) ──
                        if (TryInvokeViaRuntimeMethod(menuInstance, ownerName)) return true;
                    }
                    else
                    {
                        MiSideCoopPlugin.Logger?.LogWarning(
                            "[Co-op] MenuButtonClicker: Menu component not found in active scene "
                          + "(probably already in-game or wrong scene). Falling back to UI Button scan.");
                    }
                }

                // ── PATH FALLBACK : ancien scan Button + text (v1.4.3) ───────
                return ClickViaUiButtonScan();
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogError(
                    $"[Co-op] MenuButtonClicker.ClickNewGame failed: {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // STRATÉGIES D'INVOCATION (v1.4.6)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Stratégie A — Harmony AccessTools.MethodDelegate.
        /// Compile dynamiquement un stub IL typé qui invoke la méthode native
        /// en bypassant les checks managed IsInstanceOfType. Marche sur les
        /// classes IL2CPP wrapped où MethodInfo.Invoke échoue.
        /// </summary>
        private static bool TryInvokeViaHarmonyDelegate(object menuInstance, string ownerName)
        {
            try
            {
                var del = AccessTools.MethodDelegate<Action>(_buttonNewGameMethod, menuInstance);
                if (del == null) return false;
                del();
                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] MenuButtonClicker: Menu.ButtonNewGame() invoked on '{ownerName}' "
                  + "(via Harmony MethodDelegate).");
                return true;
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] MenuButtonClicker.Harmony-delegate path failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stratégie B — UnityEvent eventStartNewGame.Invoke().
        /// Le dump MiSide expose <c>public UnityEvent eventStartNewGame</c>
        /// (dump.cs:103884). UnityEvent est UnityEngine.CoreModule, ses
        /// méthodes survivent au stripping IL2CPP de MiSide.
        ///
        /// Note : ce path skip l'animation/audio que ButtonNewGame() joue
        /// AVANT d'invoker l'event, mais lance bien la partie.
        /// </summary>
        private static bool TryInvokeViaUnityEvent(object menuInstance, string ownerName)
        {
            try
            {
                var runtimeType = menuInstance.GetType();
                var field = runtimeType.GetField("eventStartNewGame",
                    BindingFlags.Public | BindingFlags.Instance);
                if (field == null) return false;

                var unityEvent = field.GetValue(menuInstance);
                if (unityEvent == null) return false;

                var invokeMethod = unityEvent.GetType().GetMethod("Invoke",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (invokeMethod == null) return false;

                invokeMethod.Invoke(unityEvent, null);
                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] MenuButtonClicker: eventStartNewGame.Invoke() called on '{ownerName}' "
                  + "(via UnityEvent path).");
                return true;
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] MenuButtonClicker.UnityEvent path failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stratégie C — MethodInfo.Invoke via runtime type (v1.4.5 baseline).
        /// Gardée en dernier recours au cas où les paths A/B ne marchent pas
        /// dans une future version Il2CppInterop.
        /// </summary>
        private static bool TryInvokeViaRuntimeMethod(object menuInstance, string ownerName)
        {
            try
            {
                var runtimeType = menuInstance.GetType();
                var runtimeMethod = runtimeType.GetMethod("ButtonNewGame",
                    BindingFlags.Public | BindingFlags.Instance);
                var methodToInvoke = runtimeMethod ?? _buttonNewGameMethod;
                methodToInvoke.Invoke(menuInstance, null);
                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] MenuButtonClicker: ButtonNewGame.Invoke succeeded on '{ownerName}' "
                  + $"(via {(runtimeMethod != null ? "runtime-type" : "cached")} MethodInfo).");
                return true;
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] MenuButtonClicker.RuntimeMethod path failed: {ex.Message}. "
                  + "Falling back to UI Button scan.");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        private static object FindMenuInstance()
        {
            foreach (var anchor in CollectAnchors())
            {
                var found = SearchMenuRecursive(anchor, 0);
                if (found != null) return found;
            }
            return null;
        }

        private static object SearchMenuRecursive(Transform t, int depth)
        {
            if (t == null || depth > 14) return null;
            var comp = TryGetMenuComponent(t);
            if (comp != null) return comp;

            int n;
            try { n = t.childCount; }
            catch { return null; }
            for (int i = 0; i < n; i++)
            {
                Transform c;
                try { c = t.GetChild(i); }
                catch { continue; }
                var found = SearchMenuRecursive(c, depth + 1);
                if (found != null) return found;
            }
            return null;
        }

        private static object TryGetMenuComponent(Transform t)
        {
            try
            {
                var ofConcrete = _il2cppTypeOfGenericDef.MakeGenericMethod(_menuType);
                var il2cppType = ofConcrete.Invoke(null, null);
                if (il2cppType == null) return null;
                var result = _gameObjectGetComponentByIl2CppType.Invoke(
                    t.gameObject, new object[] { il2cppType });
                return result; // null si pas de Menu sur ce GameObject
            }
            catch { return null; }
        }

        private static Transform[] CollectAnchors()
        {
            var list = new List<Transform>();
            var seen = new HashSet<int>();

            void TryAdd(Transform tr)
            {
                if (tr == null) return;
                int id;
                try { id = tr.GetInstanceID(); } catch { return; }
                if (seen.Add(id)) list.Add(tr);
            }

            // 1) Camera.main root
            try
            {
                var cam = Camera.main;
                if (cam != null) TryAdd(cam.transform.root);
            }
            catch { }

            // 2) EventSystem root (souvent root du Canvas UI)
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es != null) TryAdd(es.transform.root);
            }
            catch { }

            // 3) Noms communs de Canvas / Menu MiSide. On en met plus que dans
            //    v1.4.3 car le menu principal peut s'appeler différemment selon
            //    la version (0.93L vs 1.0+).
            string[] candidates = {
                "Menu", "MainMenu", "MenuPrincipal", "Menu Principal",
                "Canvas", "MainCanvas", "MenuCanvas", "UICanvas",
                "UI", "UIRoot", "GUI",
                "MenuMita", "MitaMenu",
            };
            foreach (var n in candidates)
            {
                try
                {
                    var go = GameObject.Find(n);
                    if (go != null) TryAdd(go.transform);
                }
                catch { }
            }

            return list.ToArray();
        }

        // ─────────────────────────────────────────────────────────────────────
        // PATH FALLBACK — ancien scan Button + text matching (v1.4.3)
        // Inchangé pour rétro-compatibilité si la classe Menu disparaît dans
        // une future version MiSide.
        // ─────────────────────────────────────────────────────────────────────
        private static readonly string[] NewGameLabels =
        {
            "NOUVELLE PARTIE",
            "NEW GAME", "NEW",
            "НОВАЯ ИГРА", "НОВАЯ",
            "NUEVA PARTIDA", "NUEVA",
            "DÉMARRER", "DEMARRER", "START",
        };

        private static bool ClickViaUiButtonScan()
        {
            try
            {
                Button btn = null;
                foreach (var anchor in CollectAnchors())
                {
                    Button[] buttons;
                    try { buttons = anchor.GetComponentsInChildrenSafe<Button>(true); }
                    catch { continue; }
                    if (buttons == null || buttons.Length == 0) continue;

                    foreach (var b in buttons)
                    {
                        if (b == null) continue;
                        var txt = ReadButtonText(b);
                        if (string.IsNullOrEmpty(txt)) continue;
                        var up = txt.Trim().ToUpperInvariant();
                        foreach (var cand in NewGameLabels)
                        {
                            var cu = cand.ToUpperInvariant();
                            if (up == cu || up.Contains(cu)) { btn = b; break; }
                        }
                        if (btn != null) break;
                    }
                    if (btn != null) break;
                }

                if (btn == null)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        "[Co-op] MenuButtonClicker fallback: 'Nouvelle Partie' button not found "
                      + "(neither via Menu.ButtonNewGame nor UI scan). Candidates tried: "
                      + string.Join(", ", NewGameLabels));
                    return false;
                }

                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] MenuButtonClicker fallback: invoking onClick on '{ReadButtonText(btn)}'.");
                try { btn.onClick.Invoke(); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogError(
                        $"[Co-op] MenuButtonClicker fallback: onClick.Invoke threw: {ex.Message}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogError(
                    $"[Co-op] MenuButtonClicker.ClickViaUiButtonScan failed: {ex.Message}");
                return false;
            }
        }

        private static string ReadButtonText(Button b)
        {
            if (b == null) return null;

            // 1) Text legacy
            try
            {
                var t = b.GetComponentInChildrenSafe<Text>(true);
                if (t != null && !string.IsNullOrEmpty(t.text)) return t.text;
            }
            catch { }

            // 2) GameObject name (filet de sécurité — souvent contient "NewGame")
            try
            {
                var name = b.gameObject?.name;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }

            return null;
        }
    }
}

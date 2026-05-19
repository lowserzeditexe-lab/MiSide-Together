using System;
using UnityEngine;
using MiSideCoop.Avatars;
using MiSideCoop.Network;
using MiSideCoop.UI;
using MiSideCoop.Relay;
using MiSideCoop.Update;

namespace MiSideCoop
{
    /// <summary>
    /// MonoBehaviour créé au démarrage du plugin. Attache tous les composants
    /// co-op sur un objet persistant (DontDestroyOnLoad).
    ///
    /// Corrections IL2CPP :
    ///   • v1.0.1 : DontDestroyOnLoad appelé depuis Awake() (contexte Unity main-thread)
    ///     et non depuis Plugin.Load() (trop tôt).
    ///   • v1.0.1 : transform.SetParent(null) imposé avant DontDestroyOnLoad pour
    ///     satisfaire la contrainte "root GameObject uniquement".
    ///   • v1.0.1 : Instance nettoyée dans OnDestroy() pour permettre la recréation
    ///     automatique via EnsureBootstrap.
    ///   • v1.1.3 : SuppressWarning Unity DDoL — la création du GO depuis
    ///     Internal_SceneLoaded postfix garantit que la scène est active.
    /// </summary>
    public class CoopBootstrap : MonoBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        public static CoopBootstrap Instance { get; private set; }

        private CoopMenuUI         _menuUI;
        private CoopNetworkManager _networkManager;

        private void Awake()
        {
            // ① Garantit que l'objet est à la racine (exigence de DontDestroyOnLoad)
            if (transform.parent != null)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    "[Co-op] Bootstrap had an unexpected parent — reparented to root.");
                transform.SetParent(null);
            }

            // ② Garde-fou singleton : si une instance existe déjà, on se suicide
            if (Instance != null)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    "[Co-op] Bootstrap instance already active — destroying duplicate.");
                Destroy(gameObject);
                return;
            }
            Instance = this;

            // ③ Rend l'objet persistant entre toutes les scènes.
            //    Diagnostic explicite : on vérifie que gameObject est bien root
            //    juste avant l'appel, pour distinguer un éventuel warning Unity
            //    qui viendrait de NOUS d'un warning qui viendrait du jeu.
            var isRoot = transform.parent == null;
            DontDestroyOnLoad(gameObject);
            MiSideCoopPlugin.Logger?.LogInfo(
                $"[Co-op] Bootstrap persisted (DontDestroyOnLoad, root={isRoot}) — menu active.");

            // ④ Attache les composants co-op sur ce même objet
            _networkManager = gameObject.AddComponent<CoopNetworkManager>();
            _menuUI         = gameObject.AddComponent<CoopMenuUI>();
            gameObject.AddComponent<GameStateSync>();
            gameObject.AddComponent<RoomManager>();
            gameObject.AddComponent<AutoUpdater>();
            gameObject.AddComponent<SharedPovController>(); // v1.6.6 (watcher désactivé en v1.6.8)
            gameObject.AddComponent<HandItemSync>();        // v1.6.8 — synchro Smartphone/Knife/...
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                MiSideCoopPlugin.Logger?.LogWarning(
                    "[Co-op] Bootstrap destroyed — will be recreated on next scene change.");
            }
        }

        // ── Raccourci clavier ─────────────────────────────────────────────────
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
                _menuUI?.ToggleMenu();

            // v1.3.7 — F9 dump scene diagnostic.
            // Imprime dans le log la liste des Animators / Cameras / Rigidbodies /
            // CharacterControllers + le candidat MC retenu par l'heuristique.
            // Sert à identifier le vrai nom du MC en MiSide 0.93L (et au-delà)
            // sans dépendre d'un dump Cpp2IL externe.
            if (Input.GetKeyDown(KeyCode.F9))
            {
                try { SceneDiagnostics.DumpScene(); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogError(
                        $"[Co-op] F9 dump failed: {ex.Message}");
                }
            }

            // v1.5.2 — F10 : dump hiérarchie complète du MC (Player) avec
            // activeSelf, renderers, Animator states. Sert à diagnostiquer
            // pourquoi la tête / les animations ne s'affichent pas sur le clone.
            if (Input.GetKeyDown(KeyCode.F10))
            {
                try { SceneDiagnostics.DumpMcHierarchy(); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogError(
                        $"[Co-op] F10 dump failed: {ex.Message}");
                }
            }
        }
    }
}

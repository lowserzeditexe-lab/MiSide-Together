using System;
using UnityEngine;
using MiSideCoop.Network;
using MiSideCoop.UI;
using MiSideCoop.Relay;

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

            // ③ Rend l'objet persistant entre toutes les scènes
            DontDestroyOnLoad(gameObject);
            MiSideCoopPlugin.Logger?.LogInfo(
                "[Co-op] Bootstrap persisted (DontDestroyOnLoad) — menu active.");

            // ④ Attache les composants co-op sur ce même objet
            _networkManager = gameObject.AddComponent<CoopNetworkManager>();
            _menuUI         = gameObject.AddComponent<CoopMenuUI>();
            gameObject.AddComponent<GameStateSync>();
            gameObject.AddComponent<RoomManager>();
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
        }
    }
}

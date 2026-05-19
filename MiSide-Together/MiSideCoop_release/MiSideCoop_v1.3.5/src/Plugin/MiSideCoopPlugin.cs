using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using MiSideCoop.Network;
using MiSideCoop.UI;
using MiSideCoop.Relay;
using MiSideCoop.Avatars;
using MiSideCoop.Update;
// using MiSideCoop.Utils;  // (BootstrapRecovery supprimé en v1.1.3)

namespace MiSideCoop
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class MiSideCoopPlugin : BasePlugin
    {
        public static ManualLogSource Logger   { get; private set; }
        public static MiSideCoopPlugin Instance { get; private set; }

        public static ConfigEntry<string> RelayHost        { get; private set; }
        public static ConfigEntry<int>    RelayPort        { get; private set; }
        public static ConfigEntry<string> Player2SkinColor { get; private set; }
        public static ConfigEntry<string> LocalPlayerName  { get; private set; }

        // ── Auto-update ───────────────────────────────────────────────────────
        public static ConfigEntry<bool>   UpdateCheckEnabled { get; private set; }
        public static ConfigEntry<string> UpdateRepo         { get; private set; }
        public static ConfigEntry<string> UpdateSkipVersion  { get; private set; }

        private Harmony _harmony;

        public override void Load()
        {
            Instance = this;
            Logger   = Log;

            // ── Configuration ────────────────────────────────────────────────
            //
            // v1.3.0 — Le serveur de relais est désormais un proxy TCP brut sur
            // un PORT UNIQUE. Plus de HTTP, plus de port P2P séparé : les deux
            // joueurs se connectent au relais qui route les paquets entre eux.
            //
            RelayHost = Config.Bind(
                "Network", "RelayHost", "82.64.128.239",
                "Hôte (IP publique ou DDNS) du serveur de relais TCP. " +
                "Tous les joueurs doivent utiliser la même valeur pour se rejoindre.");

            RelayPort = Config.Bind(
                "Network", "RelayPort", 8001,
                "Port TCP du serveur de relais. Doit être ouvert sur le routeur " +
                "de la machine qui héberge le relais.");

            Player2SkinColor = Config.Bind(
                "Avatar", "Player2SkinColor", "Blue",
                "Couleur du skin du Joueur 2. Valeurs : Red, Blue, Green, Yellow, Purple, Orange, White.");

            LocalPlayerName = Config.Bind(
                "General", "PlayerName", "Player2",
                "Votre pseudo affiché au-dessus de votre avatar sur l'écran de l'autre joueur.");

            UpdateCheckEnabled = Config.Bind(
                "Updates", "CheckForUpdates", true,
                "Vérifie au démarrage si une nouvelle version du mod est disponible sur GitHub. " +
                "Une popup permet d'installer la mise à jour automatiquement (redémarrage du jeu).");

            UpdateRepo = Config.Bind(
                "Updates", "GitHubRepo", "lowserzeditexe-lab/MiSide-Together",
                "Dépôt GitHub utilisé pour les releases. Format: 'owner/repo'.");

            UpdateSkipVersion = Config.Bind(
                "Updates", "SkipVersion", "",
                "Version explicitement skippée par l'utilisateur (rempli automatiquement par le bouton SKIP). " +
                "Vider pour réactiver la proposition de mise à jour vers cette version.");

            // ── Enregistrement des types IL2CPP ───────────────────────────────
            RegisterIl2CppTypes();

            // ── Patches Harmony ───────────────────────────────────────────────
            _harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            _harmony.PatchAll();

            Logger.LogInfo($"[MiSide Together] v{PluginInfo.PLUGIN_VERSION} loaded successfully.");
            Logger.LogInfo("[MiSide Together] The co-op menu will open on first scene load. Press F8 to toggle.");

            // ── Bootstrap Unity ───────────────────────────────────────────────
            // IMPORTANT : NE PAS appeler EnsureBootstrap() ici !
            // Pendant la phase Load() de BepInEx 6, Unity n'a pas encore initialisé
            // son système de scènes → DontDestroyOnLoad ne fonctionne pas.
            // Le bootstrap sera créé dans SceneManagerInternalLoadedPatch.Postfix
            // qui s'exécute lors du PREMIER chargement de scène Unity (contexte OK).
        }

        /// <summary>
        /// Crée le bootstrap co-op si nécessaire.
        ///
        /// Appelée UNIQUEMENT depuis le patch <c>Internal_SceneLoaded</c> (postfix),
        /// qui s'exécute après l'initialisation complète du système de scènes Unity
        /// → <c>DontDestroyOnLoad</c> fonctionne correctement à ce moment précis
        /// (preuve : le log "Bootstrap marqué DontDestroyOnLoad – menu co-op actif"
        /// s'affiche sans warning depuis v1.0.x).
        ///
        /// v1.1.3 : on a SUPPRIMÉ les appels redondants depuis les patches
        /// <c>LoadScene*</c> (postfix) qui spawnaient un BootstrapRecovery en
        /// pleine transition de scène — c'est cet appel tardif qui déclenchait
        /// le warning "DontDestroyOnLoad only works for root GameObjects" car
        /// le GO créé n'était pas dans la scène active à ce moment-là.
        /// Internal_SceneLoaded fire pour TOUTES les transitions de scène
        /// (sync, async, additive), donc SpawnRecovery était redondant.
        /// </summary>
        public static void EnsureBootstrap()
        {
            if (CoopBootstrap.Instance != null) return;

            Logger?.LogInfo("[Co-op] Bootstrapping co-op runtime...");
            var go = new GameObject("MiSideCoopBootstrap");
            // DontDestroyOnLoad sera appelé dans CoopBootstrap.Awake()
            go.AddComponent<CoopBootstrap>();
        }

        // ── Enregistrement IL2CPP ─────────────────────────────────────────────
        private static void RegisterIl2CppTypes()
        {
            ClassInjector.RegisterTypeInIl2Cpp<CoopBootstrap>();
            ClassInjector.RegisterTypeInIl2Cpp<CoopNetworkManager>();
            ClassInjector.RegisterTypeInIl2Cpp<AvatarAnimatorSync>();
            ClassInjector.RegisterTypeInIl2Cpp<GameStateSync>();
            ClassInjector.RegisterTypeInIl2Cpp<CoopMenuUI>();
            ClassInjector.RegisterTypeInIl2Cpp<RoomManager>();
            ClassInjector.RegisterTypeInIl2Cpp<Player1Avatar>();
            ClassInjector.RegisterTypeInIl2Cpp<Player2Avatar>();
            ClassInjector.RegisterTypeInIl2Cpp<AutoUpdater>();
            // BootstrapRecovery supprimé en v1.1.3 — Internal_SceneLoaded couvre
            // 100 % des transitions de scène (cf. ScenePatch.cs).
        }
    }
}

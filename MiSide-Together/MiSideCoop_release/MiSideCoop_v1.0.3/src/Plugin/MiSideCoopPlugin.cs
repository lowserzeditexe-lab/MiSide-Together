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
using MiSideCoop.Utils;

namespace MiSideCoop
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class MiSideCoopPlugin : BasePlugin
    {
        public static ManualLogSource Logger   { get; private set; }
        public static MiSideCoopPlugin Instance { get; private set; }

        public static ConfigEntry<string> RelayServerUrl   { get; private set; }
        public static ConfigEntry<int>    NetworkPort      { get; private set; }
        public static ConfigEntry<string> Player2SkinColor { get; private set; }
        public static ConfigEntry<string> LocalPlayerName  { get; private set; }

        private Harmony _harmony;

        public override void Load()
        {
            Instance = this;
            Logger   = Log;

            // ── Configuration ────────────────────────────────────────────────
            RelayServerUrl = Config.Bind(
                "Network", "RelayServerUrl", "http://localhost:3000",
                "URL du serveur de relais Node.js. Lancez relay-server.js sur votre propre serveur.");

            NetworkPort = Config.Bind(
                "Network", "Port", 7777,
                "Port TCP. L'hôte doit l'ouvrir dans son pare-feu.");

            Player2SkinColor = Config.Bind(
                "Avatar", "Player2SkinColor", "Blue",
                "Couleur du skin du Joueur 2. Valeurs : Red, Blue, Green, Yellow, Purple, Orange, White.");

            LocalPlayerName = Config.Bind(
                "General", "PlayerName", "Player2",
                "Votre pseudo affiché au-dessus de votre avatar sur l'écran de l'autre joueur.");

            // ── Enregistrement des types IL2CPP ───────────────────────────────
            RegisterIl2CppTypes();

            // ── Patches Harmony ───────────────────────────────────────────────
            _harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            _harmony.PatchAll();

            Logger.LogInfo($"[MiSide Co-op] v{PluginInfo.PLUGIN_VERSION} chargé avec succès.");
            Logger.LogInfo("[MiSide Co-op] Le menu co-op s'ouvrira dès la première scène. F8 pour fermer/rouvrir.");

            // ── Bootstrap Unity ───────────────────────────────────────────────
            // IMPORTANT : NE PAS appeler EnsureBootstrap() ici !
            // Pendant la phase Load() de BepInEx 6, Unity n'a pas encore initialisé
            // son système de scènes → DontDestroyOnLoad ne fonctionne pas.
            // Le bootstrap sera créé dans SceneManagerInternalLoadedPatch.Postfix
            // qui s'exécute lors du PREMIER chargement de scène Unity (contexte OK).
        }

        /// <summary>
        /// Crée le bootstrap co-op si nécessaire.
        /// DOIT être appelé uniquement depuis des patches qui s'exécutent après
        /// l'initialisation complète du système de scènes Unity (pas depuis Load()).
        /// </summary>
        public static void EnsureBootstrap()
        {
            if (CoopBootstrap.Instance != null) return;

            Logger?.LogInfo("[Co-op] Création du bootstrap co-op...");
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
            ClassInjector.RegisterTypeInIl2Cpp<BootstrapRecovery>();
        }
    }
}

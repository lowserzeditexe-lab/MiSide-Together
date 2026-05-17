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

namespace MiSideCoop
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class MiSideCoopPlugin : BasePlugin
    {
        public static ManualLogSource Logger  { get; private set; }
        public static MiSideCoopPlugin Instance { get; private set; }

        public static ConfigEntry<string> RelayServerUrl    { get; private set; }
        public static ConfigEntry<int>    NetworkPort       { get; private set; }
        public static ConfigEntry<string> Player2SkinColor  { get; private set; }
        public static ConfigEntry<string> LocalPlayerName   { get; private set; }

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
                "Port TCP Mirror/Telepathy. L'hôte doit l'ouvrir dans son pare-feu.");

            Player2SkinColor = Config.Bind(
                "Avatar", "Player2SkinColor", "Blue",
                "Couleur du skin du Joueur 2. Valeurs acceptées : Red, Blue, Green, Yellow, Purple, Orange, White.");

            LocalPlayerName = Config.Bind(
                "General", "PlayerName", "Player2",
                "Votre pseudo affiché au-dessus de votre avatar sur l'écran de l'autre joueur.");

            // ── Enregistrement des types IL2CPP ───────────────────────────────
            RegisterIl2CppTypes();

            // ── Patches Harmony ───────────────────────────────────────────────
            _harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            _harmony.PatchAll();

            Logger.LogInfo($"[MiSide Co-op] v{PluginInfo.PLUGIN_VERSION} chargé avec succès.");
            Logger.LogInfo("[MiSide Co-op] Appuyez sur F8 pour ouvrir le menu co-op.");

            // ── Bootstrap Unity ───────────────────────────────────────────────
            var bootstrapGo = new GameObject("MiSideCoopBootstrap");
            Object.DontDestroyOnLoad(bootstrapGo);
            bootstrapGo.AddComponent<CoopBootstrap>();
        }

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
        }
    }
}

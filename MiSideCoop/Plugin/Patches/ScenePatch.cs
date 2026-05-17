using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using MiSideCoop.Network;
using MiSideCoop.Utils;

namespace MiSideCoop.Patches
{
    /// <summary>
    /// Patches sur SceneManager pour :
    ///   1. Recréer automatiquement le bootstrap co-op à chaque changement de scène.
    ///   2. Loguer les transitions pour le mode co-op.
    ///
    /// Stratégie IL2CPP :
    ///   • SceneManager.sceneLoaded est strippé dans ce build → inutilisable.
    ///   • Patch PRIMAIRE : Internal_SceneLoaded (méthode interne Unity appelée par le
    ///     moteur natif pour TOUT changement de scène, y compris le tout premier).
    ///   • Patches SECONDAIRES : toutes les surcharges publiques de LoadScene /
    ///     LoadSceneAsync (on ne sait pas laquelle MiSide utilise).
    ///   • EnsureBootstrap() doit être appelé UNIQUEMENT depuis ces patches (pas depuis
    ///     Load() où Unity n'est pas encore initialisé).
    /// </summary>

    // ══════════════════════════════════════════════════════════════════════════
    // PATCH PRIMAIRE — capture 100 % des transitions de scène
    // ══════════════════════════════════════════════════════════════════════════

    [HarmonyPatch(typeof(SceneManager), "Internal_SceneLoaded")]
    public static class SceneManagerInternalLoadedPatch
    {
        /// <summary>
        /// Fires après CHAQUE chargement de scène (même le tout premier).
        /// Exécuté dans le contexte Unity pleinement initialisé → DontDestroyOnLoad fonctionne.
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(Scene scene, LoadSceneMode mode)
        {
            MiSideCoopPlugin.EnsureBootstrap();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PATCHES SECONDAIRES — filet de sécurité sur toutes les surcharges publiques
    // ══════════════════════════════════════════════════════════════════════════

    // ── LoadScene(string) ─────────────────────────────────────────────────────
    [HarmonyPatch(typeof(SceneManager), nameof(SceneManager.LoadScene),
        new[] { typeof(string) })]
    public static class ScenePatchByNameOnly
    {
        [HarmonyPrefix]
        public static bool Prefix(string sceneName) => true;

        [HarmonyPostfix]
        public static void Postfix(string sceneName)
            => ScenePatchHelper.SpawnRecovery();
    }

    // ── LoadScene(string, LoadSceneMode) ──────────────────────────────────────
    [HarmonyPatch(typeof(SceneManager), nameof(SceneManager.LoadScene),
        new[] { typeof(string), typeof(LoadSceneMode) })]
    public static class ScenePatchByName
    {
        [HarmonyPrefix]
        public static bool Prefix(string sceneName, LoadSceneMode mode)
        {
            ScenePatchHelper.LogCoopScene(sceneName, mode);
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(string sceneName, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single)
                ScenePatchHelper.SpawnRecovery();
        }
    }

    // ── LoadScene(int) ────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(SceneManager), nameof(SceneManager.LoadScene),
        new[] { typeof(int) })]
    public static class ScenePatchByIndexOnly
    {
        [HarmonyPrefix]
        public static bool Prefix(int sceneBuildIndex) => true;

        [HarmonyPostfix]
        public static void Postfix(int sceneBuildIndex)
            => ScenePatchHelper.SpawnRecovery();
    }

    // ── LoadScene(int, LoadSceneMode) ─────────────────────────────────────────
    [HarmonyPatch(typeof(SceneManager), nameof(SceneManager.LoadScene),
        new[] { typeof(int), typeof(LoadSceneMode) })]
    public static class ScenePatchByIndex
    {
        [HarmonyPrefix]
        public static bool Prefix(int sceneBuildIndex, LoadSceneMode mode) => true;

        [HarmonyPostfix]
        public static void Postfix(int sceneBuildIndex, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single)
                ScenePatchHelper.SpawnRecovery();
        }
    }

    // ── LoadSceneAsync(string) ────────────────────────────────────────────────
    [HarmonyPatch(typeof(SceneManager), nameof(SceneManager.LoadSceneAsync),
        new[] { typeof(string) })]
    public static class ScenePatchAsyncNameOnly
    {
        [HarmonyPrefix]
        public static bool Prefix(string sceneName) => true;

        [HarmonyPostfix]
        public static void Postfix(string sceneName)
            => ScenePatchHelper.SpawnRecovery();
    }

    // ── LoadSceneAsync(string, LoadSceneMode) ─────────────────────────────────
    [HarmonyPatch(typeof(SceneManager), nameof(SceneManager.LoadSceneAsync),
        new[] { typeof(string), typeof(LoadSceneMode) })]
    public static class ScenePatchAsync
    {
        [HarmonyPrefix]
        public static bool Prefix(string sceneName, LoadSceneMode mode) => true;

        [HarmonyPostfix]
        public static void Postfix(string sceneName, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single)
                ScenePatchHelper.SpawnRecovery();
        }
    }

    // ── LoadSceneAsync(int) ───────────────────────────────────────────────────
    [HarmonyPatch(typeof(SceneManager), nameof(SceneManager.LoadSceneAsync),
        new[] { typeof(int) })]
    public static class ScenePatchAsyncIndexOnly
    {
        [HarmonyPrefix]
        public static bool Prefix(int sceneBuildIndex) => true;

        [HarmonyPostfix]
        public static void Postfix(int sceneBuildIndex)
            => ScenePatchHelper.SpawnRecovery();
    }

    // ── LoadSceneAsync(int, LoadSceneMode) ────────────────────────────────────
    [HarmonyPatch(typeof(SceneManager), nameof(SceneManager.LoadSceneAsync),
        new[] { typeof(int), typeof(LoadSceneMode) })]
    public static class ScenePatchAsyncIndex
    {
        [HarmonyPrefix]
        public static bool Prefix(int sceneBuildIndex, LoadSceneMode mode) => true;

        [HarmonyPostfix]
        public static void Postfix(int sceneBuildIndex, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single)
                ScenePatchHelper.SpawnRecovery();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Crée un BootstrapRecovery persistant (DontDestroyOnLoad) qui s'assure
    /// que le bootstrap co-op est recréé dans la nouvelle scène si nécessaire.
    /// </summary>
    internal static class ScenePatchHelper
    {
        internal static void SpawnRecovery()
        {
            var go = new GameObject("_MiCoopRecovery_");
            go.AddComponent<BootstrapRecovery>();
            // DontDestroyOnLoad est appelé dans BootstrapRecovery.Awake()
        }

        internal static void LogCoopScene(string sceneName, LoadSceneMode mode)
        {
            var nm = CoopNetworkManager.Instance;
            if (nm == null || !nm.IsConnected) return;
            MiSideCoopPlugin.Logger?.LogDebug(
                $"[ScenePatch] '{sceneName}' ({mode}) — co-op {(nm.IsHost ? "hôte" : "client")}.");
        }
    }

    // ── Méthode utilitaire accessible depuis les patches ──────────────────────
}

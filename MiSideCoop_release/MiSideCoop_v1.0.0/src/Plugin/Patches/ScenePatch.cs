using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using MiSideCoop.Network;
using MiSideCoop.Utils;

namespace MiSideCoop.Patches
{
    /// <summary>
    /// Patch SceneManager.LoadScene pour :
    ///   1. Synchroniser les changements de scène entre hôte et invité (comportement original).
    ///   2. S'assurer que le bootstrap co-op est recréé après chaque transition (v1.1).
    ///
    /// Robustesse IL2CPP :
    ///   SceneManager.sceneLoaded est strippé dans ce build IL2CPP — on ne peut pas s'y
    ///   abonner. On utilise donc un Postfix sur LoadScene pour créer un BootstrapRecovery
    ///   (DontDestroyOnLoad) qui appelle EnsureBootstrap() dans le contexte nouvelle scène.
    /// </summary>

    // ── Patch par nom ─────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(SceneManager), nameof(SceneManager.LoadScene),
        new[] { typeof(string), typeof(LoadSceneMode) })]
    public static class ScenePatchByName
    {
        [HarmonyPrefix]
        public static bool Prefix(string sceneName, LoadSceneMode mode)
        {
            var nm = CoopNetworkManager.Instance;
            if (nm == null || !nm.IsConnected) return true;

            if (!nm.IsHost)
            {
                MiSideCoopPlugin.Logger.LogDebug(
                    $"[ScenePatch] Client : chargement de '{sceneName}' autorisé.");
                return true;
            }

            MiSideCoopPlugin.Logger.LogDebug(
                $"[ScenePatch] Hôte : chargement de scène '{sceneName}' → sera synchronisé.");
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(string sceneName, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single)
                ScenePatchHelper.SpawnRecovery();
        }
    }

    // ── Patch par index ───────────────────────────────────────────────────────
    [HarmonyPatch(typeof(SceneManager), nameof(SceneManager.LoadScene),
        new[] { typeof(int), typeof(LoadSceneMode) })]
    public static class ScenePatchByIndex
    {
        [HarmonyPrefix]
        public static bool Prefix(int sceneBuildIndex, LoadSceneMode mode)
        {
            var nm = CoopNetworkManager.Instance;
            if (nm == null || !nm.IsConnected) return true;

            if (nm.IsHost)
            {
                string scenePath = SceneUtility.GetScenePathByBuildIndex(sceneBuildIndex);
                MiSideCoopPlugin.Logger.LogDebug(
                    $"[ScenePatch] Hôte : chargement scène index {sceneBuildIndex} ({scenePath}).");
            }
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(int sceneBuildIndex, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single)
                ScenePatchHelper.SpawnRecovery();
        }
    }

    // ── Patch async ───────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(SceneManager), nameof(SceneManager.LoadSceneAsync),
        new[] { typeof(string), typeof(LoadSceneMode) })]
    public static class ScenePatchAsync
    {
        [HarmonyPrefix]
        public static bool Prefix(string sceneName, LoadSceneMode mode)
        {
            var nm = CoopNetworkManager.Instance;
            if (nm == null || !nm.IsConnected) return true;

            MiSideCoopPlugin.Logger.LogDebug(
                $"[ScenePatch] LoadSceneAsync '{sceneName}' intercepté.");
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(string sceneName, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single)
                ScenePatchHelper.SpawnRecovery();
        }
    }

    // ── Helper interne partagé ────────────────────────────────────────────────
    internal static class ScenePatchHelper
    {
        /// <summary>
        /// Crée un BootstrapRecovery persistant (DontDestroyOnLoad) qui recréera
        /// le bootstrap co-op dans la nouvelle scène si nécessaire.
        /// </summary>
        internal static void SpawnRecovery()
        {
            var go = new GameObject("_MiCoopRecovery_");
            // DontDestroyOnLoad sera appelé dans BootstrapRecovery.Awake()
            go.AddComponent<BootstrapRecovery>();
        }
    }
}

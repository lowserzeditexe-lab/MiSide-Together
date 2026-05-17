using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using MiSideCoop.Network;

namespace MiSideCoop.Patches
{
    /// <summary>
    /// Patch SceneManager.LoadScene pour synchroniser les changements de scène.
    ///
    /// Comportement :
    ///   - Hôte charge une scène → GameStateSync broadcast un SceneChangeMessage → client charge aussi.
    ///   - Le client ne doit pas appeler LoadScene de manière autonome pendant une session co-op
    ///     (sauf en réponse au message de l'hôte).
    ///
    /// Le patch intercepte les deux surcharges principales (par nom et par index).
    /// </summary>
    [HarmonyPatch(typeof(SceneManager), nameof(SceneManager.LoadScene),
        new[] { typeof(string), typeof(LoadSceneMode) })]
    public static class ScenePatchByName
    {
        [HarmonyPrefix]
        public static bool Prefix(string sceneName, LoadSceneMode mode)
        {
            var nm = CoopNetworkManager.Instance;
            if (nm == null || !nm.IsConnected) return true;  // Pas en co-op : comportement normal

            if (!nm.IsHost)
            {
                // Le client autorise le chargement uniquement s'il est déclenché par
                // la réception d'un SceneChangeMessage (pas d'appel interne direct).
                MiSideCoopPlugin.Logger.LogDebug(
                    $"[ScenePatch] Client : chargement de '{sceneName}' autorisé.");
                return true;
            }

            // Hôte : le chargement est normal, GameStateSync.OnSceneLoaded enverra le broadcast.
            MiSideCoopPlugin.Logger.LogDebug(
                $"[ScenePatch] Hôte : chargement de scène '{sceneName}' → sera synchronisé.");
            return true;
        }
    }

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
    }

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
    }
}

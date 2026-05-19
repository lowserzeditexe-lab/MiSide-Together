using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using MiSideCoop.Network;
// using MiSideCoop.Utils;  // (BootstrapRecovery supprimé en v1.1.3)

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
    // PATCHES SECONDAIRES — log uniquement (v1.1.3 : SpawnRecovery supprimé)
    // ══════════════════════════════════════════════════════════════════════════
    //
    // Pourquoi : le patch primaire Internal_SceneLoaded fire pour TOUTES les
    // transitions de scène (sync, async, additive). Les anciens postfix
    // LoadScene* spawnaient un BootstrapRecovery (new GameObject + DDoL) en
    // pleine transition de scène → Unity émettait
    //   "DontDestroyOnLoad only works for root GameObjects"
    // car la nouvelle scène n'était pas encore active à ce moment précis.
    // Solution : on ne conserve que les Prefix pour logger les transitions,
    // tout le travail de recréation est délégué à Internal_SceneLoaded.

    // ── LoadScene(string, LoadSceneMode) — log seulement ──────────────────────
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
    }

    // ══════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ══════════════════════════════════════════════════════════════════════════

    internal static class ScenePatchHelper
    {
        internal static void LogCoopScene(string sceneName, LoadSceneMode mode)
        {
            var nm = CoopNetworkManager.Instance;
            if (nm == null || !nm.IsConnected) return;
            MiSideCoopPlugin.Logger?.LogDebug(
                $"[ScenePatch] '{sceneName}' ({mode}) — co-op {(nm.IsHost ? "hôte" : "client")}.");
        }
    }
}

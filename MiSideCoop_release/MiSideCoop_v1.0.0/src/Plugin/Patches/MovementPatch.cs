using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using MiSideCoop.Network;

namespace MiSideCoop.Patches
{
    /// <summary>
    /// Patch du système de déplacement du MC de MiSide.
    /// Classe ciblée : PlayerMove (TypeDefIndex 2158 dans le dump IL2CPP).
    ///
    /// Méthodes patchées :
    ///   • Update()      – bloque les inputs sur les avatars distants
    ///   • FixedUpdate() – idem (physique)
    ///
    /// Résolution : on n'a pas de référence directe au type PlayerMove
    /// (les classes du jeu ne sont pas exposées au compile-time), donc on
    /// utilise AccessTools.TypeByName + TargetMethod() Harmony.
    /// </summary>
    [HarmonyPatch]
    public static class MovementPatch_Update
    {
        public static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("PlayerMove");
            return t != null ? AccessTools.Method(t, "Update") : null;
        }

        [HarmonyPrefix]
        public static bool Prefix(MonoBehaviour __instance)
            => MovementPatch.BlockIfRemoteAvatar(__instance);
    }

    [HarmonyPatch]
    public static class MovementPatch_FixedUpdate
    {
        public static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("PlayerMove");
            return t != null ? AccessTools.Method(t, "FixedUpdate") : null;
        }

        [HarmonyPrefix]
        public static bool Prefix(MonoBehaviour __instance)
            => MovementPatch.BlockIfRemoteAvatar(__instance);
    }

    public static class MovementPatch
    {
        /// <summary>
        /// Retourne false (bloque l'exécution) si l'instance est un avatar distant.
        /// </summary>
        public static bool BlockIfRemoteAvatar(MonoBehaviour __instance)
        {
            if (CoopNetworkManager.Instance == null || !CoopNetworkManager.Instance.IsConnected)
                return true;

            string name = __instance?.gameObject?.name ?? string.Empty;
            return !name.Contains("Remote") && !name.Contains("Guest") && !name.Contains("Player2_");
        }

        public static void NotifyMovement(Vector3 position, Quaternion rotation,
                                          float speed, string animState)
        {
            MiSideCoopPlugin.Logger.LogDebug(
                $"[MovementPatch] état : {animState} speed={speed:F2}");
        }
    }
}

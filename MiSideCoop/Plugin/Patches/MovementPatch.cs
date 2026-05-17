using System;
using UnityEngine;
using MiSideCoop.Network;

namespace MiSideCoop.Patches
{
    /// <summary>
    /// Patch du système de déplacement du MC.
    ///
    /// Rôle dual :
    ///   1. Empêche tout input de contrôler les avatars distants (objets portant
    ///      un nom contenant "Remote" ou "Guest").
    ///   2. Expose NotifyMovement() pour que les autres systèmes puissent
    ///      envoyer des mises à jour de mouvement ponctuelles au réseau.
    ///
    /// Note d'intégration :
    ///   Les noms réels des classes de MiSide ne sont pas publics. Utilisez dnSpy
    ///   ou ILSpy pour décompiler Assembly-CSharp.dll et remplacez les noms
    ///   dans les attributs [HarmonyPatch] si nécessaire.
    ///   La méthode statique PatchIfTypeExists() peut être appelée depuis
    ///   MiSideCoopPlugin.Load() pour patcher dynamiquement si le type est trouvé.
    /// </summary>
    public static class MovementPatch
    {
        // ── Méthode utilitaire : bloque input sur avatar distant ──────────────

        /// <summary>
        /// Retourne false (bloque l'exécution) si l'instance est un avatar distant.
        /// À utiliser comme HarmonyPrefix sur la méthode Update du PlayerController.
        /// </summary>
        public static bool BlockIfRemoteAvatar(MonoBehaviour __instance)
        {
            if (CoopNetworkManager.Instance == null || !CoopNetworkManager.Instance.IsConnected)
                return true;

            string name = __instance?.gameObject?.name ?? string.Empty;
            return !name.Contains("Remote") && !name.Contains("Guest") && !name.Contains("Player2_");
        }

        /// <summary>
        /// Notifie un changement d'état de mouvement (appel manuel depuis les patches
        /// une fois les vraies classes de MiSide identifiées).
        /// </summary>
        public static void NotifyMovement(Vector3 position, Quaternion rotation,
                                          float speed, string animState)
        {
            // Le CoopNetworkManager envoie déjà l'état à cadence fixe dans Update().
            // Cette méthode permet un envoi immédiat pour les changements importants.
            MiSideCoopPlugin.Logger.LogDebug(
                $"[MovementPatch] état : {animState} speed={speed:F2}");
        }

        // ── Exemple de patch à activer après décompilation ───────────────────
        // Décommentez et ajustez le namespace/type une fois identifié :
        //
        // [HarmonyPatch(typeof(PlayerController), "Update")]
        // [HarmonyPrefix]
        // public static bool Prefix(MonoBehaviour __instance)
        //     => BlockIfRemoteAvatar(__instance);
    }
}

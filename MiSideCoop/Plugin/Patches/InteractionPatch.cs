using System;
using UnityEngine;
using MiSideCoop.Network;

namespace MiSideCoop.Patches
{
    /// <summary>
    /// Intercepte les interactions du joueur avec les objets du monde
    /// et broadcaste un PlayerActionMessage pour synchroniser l'animation
    /// "Interact" sur l'avatar de l'autre joueur.
    ///
    /// Intégration :
    ///   Appelez NotifyInteraction() depuis le prefix/postfix du patch Harmony
    ///   sur la méthode Interact() / OnInteract() du PlayerController de MiSide.
    ///   Pour les dialogues avec Mita, appelez NotifyDialogueStart/End().
    /// </summary>
    public static class InteractionPatch
    {
        // ── Interaction générique (touche d'interaction pressée) ──────────────

        /// <summary>
        /// À appeler depuis le patch postfix de la méthode d'interaction.
        /// </summary>
        /// <param name="interactingPlayer">Le GameObject du joueur local.</param>
        /// <param name="target">L'objet interactif ciblé.</param>
        public static void NotifyInteraction(GameObject interactingPlayer, GameObject target)
        {
            var nm = CoopNetworkManager.Instance;
            if (nm == null || !nm.IsConnected) return;
            if (!IsLocalPlayerObject(interactingPlayer, nm)) return;

            nm.BroadcastAction(new PlayerActionMessage
            {
                ActionType     = "Interact",
                ActionPosition = interactingPlayer.transform.position,
                TargetObjectId = target?.name ?? string.Empty
            });

            MiSideCoopPlugin.Logger.LogDebug(
                $"[InteractionPatch] Interaction broadcastée sur : {target?.name}");
        }

        // ── Dialogue / Cutscene avec Mita ─────────────────────────────────────

        /// <summary>Appelé quand un dialogue Mita démarre (côté hôte uniquement).</summary>
        public static void NotifyDialogueStart(string dialogueId)
        {
            GameStateSync.Instance?.BroadcastCutscene(dialogueId, true);
            MiSideCoopPlugin.Logger.LogInfo($"[InteractionPatch] Dialogue démarré : {dialogueId}");
        }

        /// <summary>Appelé quand un dialogue Mita se termine (côté hôte uniquement).</summary>
        public static void NotifyDialogueEnd(string dialogueId)
        {
            GameStateSync.Instance?.BroadcastCutscene(dialogueId, false);
            MiSideCoopPlugin.Logger.LogInfo($"[InteractionPatch] Dialogue terminé : {dialogueId}");
        }

        // ── Déterminer si le GameObject est bien le joueur local ──────────────
        private static bool IsLocalPlayerObject(GameObject go, CoopNetworkManager nm)
        {
            if (go == null) return false;
            string name = go.name;
            // Exclure les avatars distants
            return !name.Contains("Remote") && !name.Contains("Guest");
        }

        // ── Exemple de patch prêt à l'emploi (décommentez après décompilation) ─
        // [HarmonyPatch(typeof(PlayerInteraction), "Interact")]
        // [HarmonyPostfix]
        // public static void Postfix(MonoBehaviour __instance, GameObject target)
        //     => NotifyInteraction(__instance.gameObject, target);
    }
}

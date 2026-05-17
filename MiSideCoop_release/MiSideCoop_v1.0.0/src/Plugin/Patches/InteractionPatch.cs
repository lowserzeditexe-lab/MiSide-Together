using System.Reflection;
using HarmonyLib;
using UnityEngine;
using MiSideCoop.Network;

namespace MiSideCoop.Patches
{
    /// <summary>
    /// Intercepte les interactions avec les objets du monde.
    /// Classe ciblée : ObjectInteractive (TypeDefIndex 2139).
    /// Méthode : Click() — invoquée quand le joueur active l'objet.
    /// </summary>
    [HarmonyPatch]
    public static class InteractionPatch_Click
    {
        public static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("ObjectInteractive");
            return t != null ? AccessTools.Method(t, "Click") : null;
        }

        [HarmonyPostfix]
        public static void Postfix(MonoBehaviour __instance)
        {
            var nm = CoopNetworkManager.Instance;
            if (nm == null || !nm.IsConnected) return;
            if (__instance == null) return;

            nm.BroadcastAction(new PlayerActionMessage
            {
                ActionType     = "Interact",
                ActionPosition = __instance.transform.position,
                TargetObjectId = __instance.gameObject.name
            });

            MiSideCoopPlugin.Logger.LogDebug(
                $"[InteractionPatch] Interaction broadcastée sur : {__instance.gameObject.name}");
        }
    }

    public static class InteractionPatch
    {
        public static void NotifyDialogueStart(string dialogueId)
        {
            GameStateSync.Instance?.BroadcastCutscene(dialogueId, true);
            MiSideCoopPlugin.Logger.LogInfo($"[InteractionPatch] Dialogue démarré : {dialogueId}");
        }

        public static void NotifyDialogueEnd(string dialogueId)
        {
            GameStateSync.Instance?.BroadcastCutscene(dialogueId, false);
            MiSideCoopPlugin.Logger.LogInfo($"[InteractionPatch] Dialogue terminé : {dialogueId}");
        }
    }
}

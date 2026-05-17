using System;
using UnityEngine;
using MiSideCoop.Network;

namespace MiSideCoop.Patches
{
    /// <summary>
    /// Synchronise le ramassage d'objets entre les deux joueurs.
    ///
    /// Quand un joueur ramasse un objet :
    ///   1. L'objet est désactivé des deux côtés via ObjectSyncMessage.
    ///   2. L'animation PickUp est jouée sur l'avatar distant via PlayerActionMessage.
    ///
    /// Intégration :
    ///   Appelez NotifyPickup() depuis le postfix Harmony de la méthode
    ///   Pickup() / OnPickup() / Collect() de la classe Item / PickupObject de MiSide.
    /// </summary>
    public static class PickupPatch
    {
        /// <summary>
        /// Notifie qu'un objet vient d'être ramassé.
        /// L'objet sera désactivé sur les deux clients.
        /// </summary>
        /// <param name="pickedObject">L'objet qui a été ramassé.</param>
        /// <param name="picker">Le joueur qui l'a ramassé.</param>
        public static void NotifyPickup(GameObject pickedObject, GameObject picker)
        {
            var nm = CoopNetworkManager.Instance;
            if (nm == null || !nm.IsConnected) return;

            // Désactivation de l'objet des deux côtés
            nm.BroadcastObjectSync(new ObjectSyncMessage
            {
                ObjectId = pickedObject.name,
                IsActive = false,
                Position = pickedObject.transform.position
            });

            // Animation PickUp sur l'avatar distant
            nm.BroadcastAction(new PlayerActionMessage
            {
                ActionType     = "PickUp",
                ActionPosition = picker != null ? picker.transform.position : pickedObject.transform.position,
                TargetObjectId = pickedObject.name
            });

            MiSideCoopPlugin.Logger.LogDebug(
                $"[PickupPatch] Ramassage de '{pickedObject.name}' synchronisé.");
        }

        // ── Exemple de patch prêt à l'emploi ─────────────────────────────────
        // [HarmonyPatch(typeof(PickupObject), "Pickup")]
        // [HarmonyPostfix]
        // public static void Postfix(MonoBehaviour __instance, GameObject player)
        //     => NotifyPickup(__instance.gameObject, player);
    }
}

using System;
using UnityEngine;
using MiSideCoop.Network;

namespace MiSideCoop.Patches
{
    /// <summary>
    /// Synchronise l'ouverture et la fermeture des portes entre les deux joueurs.
    ///
    /// Quand l'hôte ouvre une porte :
    ///   1. Un ObjectSyncMessage est envoyé avec la nouvelle rotation de la porte.
    ///   2. Un PlayerActionMessage "OpenDoor" déclenche l'animation sur l'avatar distant.
    ///
    /// Côté client, OnClientReceiveObjectSync dans CoopNetworkManager applique
    /// la rotation reçue sur l'objet porte correspondant.
    ///
    /// Intégration :
    ///   Appelez NotifyDoorStateChange() depuis le postfix Harmony de la méthode
    ///   Open() / Toggle() de la classe Door (ou équivalent dans MiSide).
    /// </summary>
    public static class DoorPatch
    {
        /// <summary>
        /// Notifie un changement d'état de porte vers l'autre joueur.
        /// </summary>
        /// <param name="door">Le GameObject de la porte.</param>
        /// <param name="isOpen">true si la porte vient d'être ouverte.</param>
        /// <param name="finalRotation">La rotation finale de la porte après ouverture/fermeture.</param>
        public static void NotifyDoorStateChange(GameObject door, bool isOpen, Quaternion finalRotation)
        {
            var nm = CoopNetworkManager.Instance;
            if (nm == null || !nm.IsConnected) return;

            // ObjectSyncMessage : on réutilise le champ Position pour stocker
            // les euler angles (porte = objet non-physique à rotation simple).
            nm.BroadcastObjectSync(new ObjectSyncMessage
            {
                ObjectId = door.name,
                IsActive = true,
                Position = finalRotation.eulerAngles
            });

            // Déclenche l'animation "OpenDoor" sur l'avatar distant
            nm.BroadcastAction(new PlayerActionMessage
            {
                ActionType     = "OpenDoor",
                ActionPosition = door.transform.position,
                TargetObjectId = door.name
            });

            MiSideCoopPlugin.Logger.LogDebug(
                $"[DoorPatch] Porte '{door.name}' synchronisée (ouverte={isOpen}).");
        }

        // ── Exemple de patch prêt à l'emploi ─────────────────────────────────
        // [HarmonyPatch(typeof(Door), "Open")]
        // [HarmonyPostfix]
        // public static void Postfix(MonoBehaviour __instance)
        //     => NotifyDoorStateChange(
        //            __instance.gameObject,
        //            true,
        //            __instance.transform.rotation);
    }
}

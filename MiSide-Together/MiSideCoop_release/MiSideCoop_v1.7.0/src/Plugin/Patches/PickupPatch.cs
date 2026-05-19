using System.Reflection;
using HarmonyLib;
using UnityEngine;
using MiSideCoop.Network;

namespace MiSideCoop.Patches
{
    /// <summary>
    /// Synchronise le ramassage d'objets entre les deux joueurs.
    ///
    /// Classes ciblées :
    ///   • ObjectInteractiveItemTake.Take()      (TypeDefIndex 2141)
    ///   • PlayerMove.TakeItem(GameObject)        (TypeDefIndex 2158)
    /// </summary>
    [HarmonyPatch]
    public static class PickupPatch_Take
    {
        public static MethodBase TargetMethod()
        {
            var t = Il2CppTypeResolver.FindType("ObjectInteractiveItemTake");
            return t != null ? AccessTools.Method(t, "Take") : null;
        }

        [HarmonyPostfix]
        public static void Postfix(MonoBehaviour __instance)
        {
            if (__instance == null) return;
            PickupPatch.NotifyPickup(__instance.gameObject, picker: null);
        }
    }

    [HarmonyPatch]
    public static class PickupPatch_TakeItem
    {
        public static MethodBase TargetMethod()
        {
            var t = Il2CppTypeResolver.FindType("PlayerMove");
            return t != null
                ? AccessTools.Method(t, "TakeItem", new[] { typeof(GameObject) })
                : null;
        }

        [HarmonyPostfix]
        public static void Postfix(MonoBehaviour __instance, GameObject _item)
        {
            if (_item == null) return;
            PickupPatch.NotifyPickup(_item, __instance?.gameObject);
        }
    }

    public static class PickupPatch
    {
        public static void NotifyPickup(GameObject pickedObject, GameObject picker)
        {
            var nm = CoopNetworkManager.Instance;
            if (nm == null || !nm.IsConnected) return;

            // v1.6.6 — On n'envoie plus ObjectSync(IsActive=false) pour les items main.
            // Avant : le peer recevait "SetActive(false)" sur son propre Smartphone
            // local → il ne voyait pas le téléphone en first-person.
            // Maintenant : le peer entre en mode "POV partagée" (cf. SharedPovController)
            // qui copie la caméra du holder pendant la séquence Mita.
            //
            // On garde l'ancien comportement uniquement pour les objets non identifiés
            // comme item-main (heuristique par mot-clé), pour ne pas casser d'autres
            // pickups potentiels (consumables au sol par ex).
            if (!IsHandHeldItem(pickedObject.name))
            {
                nm.BroadcastObjectSync(new ObjectSyncMessage
                {
                    ObjectId = pickedObject.name,
                    IsActive = false,
                    Position = pickedObject.transform.position
                });
            }

            nm.BroadcastAction(new PlayerActionMessage
            {
                ActionType     = "PickUp",
                ActionPosition = picker != null ? picker.transform.position : pickedObject.transform.position,
                TargetObjectId = pickedObject.name
            });

            // v1.6.8 — DÉSACTIVÉ. La "POV partagée" disable la MainCamera du
            // viewer, ce qui l'empêche de voir SON propre téléphone/couteau
            // en 1ère personne. L'utilisateur veut LES DEUX joueurs voir
            // l'item dans leurs propres mains. La nouvelle synchro est gérée
            // par HandItemSync (polling activeInHierarchy + broadcast).
            // (POV-share désactivée — voir HandItemSync pour la nouvelle synchro.)

            MiSideCoopPlugin.Logger.LogDebug(
                $"[PickupPatch] Ramassage de '{pickedObject.name}' synchronisé.");
        }

        // v1.6.6 — Items connus pour être tenus en main (heuristique extensible).
        // Pour ces items, on n'envoie pas ObjectSync(IsActive=false) destructeur.
        private static bool IsHandHeldItem(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lo = name.ToLowerInvariant();
            return lo.Contains("smartphone") || lo.Contains("phone")
                || lo.Contains("tetris")     || lo.Contains("gameboy")
                || lo.Contains("knife")      || lo.Contains("flashlight")
                || lo.Contains("torch");
        }

        // v1.6.6 — Items qui déclenchent le mode POV partagée (séquence scriptée
        // visible chez un seul joueur, l'autre regarde par-dessus son épaule).
        // Pour l'instant : Smartphone uniquement. À étendre selon besoins.
        private static bool IsPovSharingItem(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lo = name.ToLowerInvariant();
            return lo.Contains("smartphone") || lo.Contains("phone");
        }
    }
}

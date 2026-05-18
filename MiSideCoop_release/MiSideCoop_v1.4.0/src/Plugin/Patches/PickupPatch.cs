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

            nm.BroadcastObjectSync(new ObjectSyncMessage
            {
                ObjectId = pickedObject.name,
                IsActive = false,
                Position = pickedObject.transform.position
            });

            nm.BroadcastAction(new PlayerActionMessage
            {
                ActionType     = "PickUp",
                ActionPosition = picker != null ? picker.transform.position : pickedObject.transform.position,
                TargetObjectId = pickedObject.name
            });

            MiSideCoopPlugin.Logger.LogDebug(
                $"[PickupPatch] Ramassage de '{pickedObject.name}' synchronisé.");
        }
    }
}

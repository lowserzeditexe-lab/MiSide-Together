using System.Reflection;
using HarmonyLib;
using UnityEngine;
using MiSideCoop.Network;

namespace MiSideCoop.Patches
{
    /// <summary>
    /// Synchronise les portes entre les deux joueurs.
    /// Classe ciblée : ObjectDoor (TypeDefIndex 2138).
    ///
    /// Méthodes patchées :
    ///   • OpenAngle(float)  – animation d'ouverture par rotation
    ///   • Lock(bool)        – (dé)verrouillage
    /// </summary>
    [HarmonyPatch]
    public static class DoorPatch_OpenAngle
    {
        public static MethodBase TargetMethod()
        {
            var t = Il2CppTypeResolver.FindType("ObjectDoor");
            return t != null ? AccessTools.Method(t, "OpenAngle", new[] { typeof(float) }) : null;
        }

        [HarmonyPostfix]
        public static void Postfix(MonoBehaviour __instance, float _angle)
        {
            if (__instance == null) return;
            DoorPatch.NotifyDoorStateChange(__instance.gameObject,
                                            isOpen: _angle != 0f,
                                            __instance.transform.rotation);
        }
    }

    [HarmonyPatch]
    public static class DoorPatch_Lock
    {
        public static MethodBase TargetMethod()
        {
            var t = Il2CppTypeResolver.FindType("ObjectDoor");
            return t != null ? AccessTools.Method(t, "Lock", new[] { typeof(bool) }) : null;
        }

        [HarmonyPostfix]
        public static void Postfix(MonoBehaviour __instance, bool x)
        {
            if (__instance == null) return;
            var nm = CoopNetworkManager.Instance;
            if (nm == null || !nm.IsConnected) return;
            nm.BroadcastObjectSync(new ObjectSyncMessage
            {
                ObjectId = "DOOR_LOCK:" + __instance.gameObject.name,
                IsActive = x,
                Position = __instance.transform.position
            });
        }
    }

    public static class DoorPatch
    {
        public static void NotifyDoorStateChange(GameObject door, bool isOpen,
                                                 Quaternion finalRotation)
        {
            var nm = CoopNetworkManager.Instance;
            if (nm == null || !nm.IsConnected) return;

            nm.BroadcastObjectSync(new ObjectSyncMessage
            {
                ObjectId = door.name,
                IsActive = true,
                Position = finalRotation.eulerAngles
            });

            nm.BroadcastAction(new PlayerActionMessage
            {
                ActionType     = "OpenDoor",
                ActionPosition = door.transform.position,
                TargetObjectId = door.name
            });

            MiSideCoopPlugin.Logger.LogDebug(
                $"[DoorPatch] Porte '{door.name}' synchronisée (ouverte={isOpen}).");
        }
    }
}

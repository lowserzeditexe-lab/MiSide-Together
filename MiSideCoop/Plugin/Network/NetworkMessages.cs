using Mirror;
using UnityEngine;

namespace MiSideCoop.Network
{
    // ── Envoyé par chaque joueur à la cadence SyncRate ──────────────────────
    public struct PlayerStateMessage : NetworkMessage
    {
        public Vector3    Position;
        public Quaternion Rotation;
        public float      MoveSpeed;
        public string     AnimationState;   // "Idle","Walk","Run","Crouch"…
        public Vector3    LookDirection;
        public string     PlayerName;
    }

    // ── Action ponctuelle (interaction, ramassage, ouverture de porte…) ──────
    public struct PlayerActionMessage : NetworkMessage
    {
        public string  ActionType;      // "Interact","PickUp","OpenDoor","Die","Crouch"
        public Vector3 ActionPosition;
        public string  TargetObjectId;  // nom du GameObject concerné
    }

    // ── Changement de scène initié par l'hôte ────────────────────────────────
    public struct SceneChangeMessage : NetworkMessage
    {
        public string  SceneName;
        public Vector3 SpawnPosition;
    }

    // ── Déclenchement / fin d'une cutscene ───────────────────────────────────
    public struct CutsceneMessage : NetworkMessage
    {
        public string CutsceneId;
        public bool   Start;       // true = démarrer, false = terminer
    }

    // ── Identification du joueur à la connexion ───────────────────────────────
    public struct RoomJoinMessage : NetworkMessage
    {
        public string PlayerName;
        public int    PlayerRole;  // 1 = Hôte, 2 = Invité
    }

    // ── Synchronisation d'état d'un objet du monde (porte, item…) ────────────
    public struct ObjectSyncMessage : NetworkMessage
    {
        public string  ObjectId;
        public bool    IsActive;
        public Vector3 Position;   // pour les portes : euler angles stockés ici
    }
}

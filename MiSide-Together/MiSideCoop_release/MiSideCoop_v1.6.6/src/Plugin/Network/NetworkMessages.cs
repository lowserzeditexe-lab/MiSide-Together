using System.IO;
using UnityEngine;

namespace MiSideCoop.Network
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Système de messages binaires custom (pas de dépendance à Mirror).
    //  Chaque message est sérialisé manuellement via BinaryWriter/BinaryReader
    //  et préfixé par un MsgId d'un octet pour le routage.
    // ─────────────────────────────────────────────────────────────────────────
    public enum MsgId : byte
    {
        PlayerState   = 1,
        PlayerAction  = 2,
        SceneChange   = 3,
        Cutscene      = 4,
        RoomJoin      = 5,
        ObjectSync    = 6,
        GameLaunch    = 7,   // v1.5.0 — host clique "Démarrer" → guest auto-click "Nouvelle Partie"
        SharedPov     = 8,   // v1.6.6 — mode POV partagée pendant la séquence téléphone Mita
        InputForward  = 9,   // v1.6.6 — peer forward sa touche E vers le holder
    }

    public interface INetMessage
    {
        MsgId Id { get; }
        void Write(BinaryWriter w);
        void Read(BinaryReader r);
    }

    internal static class NetIO
    {
        public static void Write(this BinaryWriter w, Vector3 v)
        { w.Write(v.x); w.Write(v.y); w.Write(v.z); }

        public static Vector3 ReadVector3(this BinaryReader r)
            => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        public static void Write(this BinaryWriter w, Quaternion q)
        { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }

        public static Quaternion ReadQuaternion(this BinaryReader r)
            => new Quaternion(r.ReadSingle(), r.ReadSingle(),
                              r.ReadSingle(), r.ReadSingle());
    }

    // ── Envoyé par chaque joueur à la cadence SyncRate ──────────────────────
    public struct PlayerStateMessage : INetMessage
    {
        public Vector3    Position;
        public Quaternion Rotation;
        public float      MoveSpeed;
        public string     AnimationState;   // "Idle","Walk","Run","Crouch"… (legacy v1.4.x)
        public Vector3    LookDirection;
        public string     PlayerName;

        // v1.5.0 — Sync animation directe par state-hash.
        // AnimatorStateHash = GetCurrentAnimatorStateInfo(0).fullPathHash
        // AnimatorNormalizedTime = GetCurrentAnimatorStateInfo(0).normalizedTime (mod 1f)
        // Comme le clone 3D partage le MÊME AnimatorController que le MC source
        // (cloné par RealMcCloner via Instantiate), les hashes sont identiques
        // → Animator.Play(hash, 0, normTime) reproduit exactement la même anim.
        // Marche pour toutes les animations MiSide (idle, marche, course, interactions,
        // cutscenes, dialogues, etc.) sans aucun nom de paramètre à deviner.
        public int   AnimatorStateHash;
        public float AnimatorNormalizedTime;

        // v1.6.0 — VRAIS noms de params Animator MiSide découverts par
        // AnimatorDiagPatch (intercept SetFloat sur 'Person' Animator local) :
        //   • 'Forward' — composante signée du mouvement local AVANT/ARRIÈRE
        //   • 'Right'   — composante signée du mouvement local GAUCHE/DROITE
        // Ils pilotent le blend tree MiSide ('Person' Animator) qui décide
        // de Idle ↔ Walk ↔ Run ↔ StrafeLeft ↔ etc. Sans ces deux floats, le
        // ghost reste figé en T-pose même si le hash est synchronisé, car le
        // controller des states de mouvement attend ces inputs vivants.
        public float MoveForward;
        public float MoveRight;

        // v1.6.1 — Head bone local rotation. Le head MiSide est piloté en
        // MOUSE LOOK direct (rotation appliquée sur l'os 'Head' hors Animator),
        // donc Animator.Play(hash) ne reproduit PAS l'inclinaison de la tête
        // du peer. On capture localRotation sur le sender et on l'écrase APRÈS
        // Animator.Update sur le receiver pour overrider toute anim qui
        // toucherait au head bone.
        public Quaternion HeadRotation;

        // v1.6.1 — Bool 'Sit' (crouch/sit pose) capturé via AnimatorDiagPatch
        // log v1.5.9 : "anim-patch SetBool: on='Person' param='Sit' value=...".
        // C'est le seul vrai bool MiSide pour le crouch.
        public bool IsCrouching;

        public MsgId Id => MsgId.PlayerState;

        public void Write(BinaryWriter w)
        {
            w.Write(Position); w.Write(Rotation); w.Write(MoveSpeed);
            w.Write(AnimationState ?? string.Empty);
            w.Write(LookDirection);
            w.Write(PlayerName ?? string.Empty);
            w.Write(AnimatorStateHash);
            w.Write(AnimatorNormalizedTime);
            // v1.6.0 — appended fields. Old clients won't read them
            // (graceful — EndOfStream in Read is caught below).
            w.Write(MoveForward);
            w.Write(MoveRight);
            // v1.6.1 — appended fields (head bone rotation + crouch bool).
            w.Write(HeadRotation);
            w.Write(IsCrouching);
        }
        public void Read(BinaryReader r)
        {
            Position = r.ReadVector3(); Rotation = r.ReadQuaternion();
            MoveSpeed = r.ReadSingle();
            AnimationState = r.ReadString();
            LookDirection = r.ReadVector3();
            PlayerName = r.ReadString();
            AnimatorStateHash = r.ReadInt32();
            AnimatorNormalizedTime = r.ReadSingle();
            // v1.6.0 — backward-compat : optional appended fields.
            try { MoveForward = r.ReadSingle(); } catch { MoveForward = 0f; }
            try { MoveRight   = r.ReadSingle(); } catch { MoveRight   = 0f; }
            // v1.6.1 — backward-compat : optional appended fields.
            try { HeadRotation = r.ReadQuaternion(); } catch { HeadRotation = Quaternion.identity; }
            try { IsCrouching  = r.ReadBoolean();    } catch { IsCrouching  = false; }
        }
    }

    // ── Action ponctuelle (interaction, ramassage, ouverture de porte…) ──────
    public struct PlayerActionMessage : INetMessage
    {
        public string  ActionType;      // "Interact","PickUp","OpenDoor","Die","Crouch"
        public Vector3 ActionPosition;
        public string  TargetObjectId;  // nom du GameObject concerné

        public MsgId Id => MsgId.PlayerAction;

        public void Write(BinaryWriter w)
        {
            w.Write(ActionType ?? string.Empty);
            w.Write(ActionPosition);
            w.Write(TargetObjectId ?? string.Empty);
        }
        public void Read(BinaryReader r)
        {
            ActionType = r.ReadString();
            ActionPosition = r.ReadVector3();
            TargetObjectId = r.ReadString();
        }
    }

    // ── Changement de scène initié par l'hôte ────────────────────────────────
    public struct SceneChangeMessage : INetMessage
    {
        public string  SceneName;
        public Vector3 SpawnPosition;

        public MsgId Id => MsgId.SceneChange;

        public void Write(BinaryWriter w)
        { w.Write(SceneName ?? string.Empty); w.Write(SpawnPosition); }
        public void Read(BinaryReader r)
        { SceneName = r.ReadString(); SpawnPosition = r.ReadVector3(); }
    }

    // ── Déclenchement / fin d'une cutscene ───────────────────────────────────
    public struct CutsceneMessage : INetMessage
    {
        public string CutsceneId;
        public bool   Start;       // true = démarrer, false = terminer

        public MsgId Id => MsgId.Cutscene;

        public void Write(BinaryWriter w)
        { w.Write(CutsceneId ?? string.Empty); w.Write(Start); }
        public void Read(BinaryReader r)
        { CutsceneId = r.ReadString(); Start = r.ReadBoolean(); }
    }

    // ── Identification du joueur à la connexion ───────────────────────────────
    public struct RoomJoinMessage : INetMessage
    {
        public string PlayerName;
        public int    PlayerRole;  // 1 = Hôte, 2 = Invité

        public MsgId Id => MsgId.RoomJoin;

        public void Write(BinaryWriter w)
        { w.Write(PlayerName ?? string.Empty); w.Write(PlayerRole); }
        public void Read(BinaryReader r)
        { PlayerName = r.ReadString(); PlayerRole = r.ReadInt32(); }
    }

    // ── Synchronisation d'état d'un objet du monde (porte, item…) ────────────
    public struct ObjectSyncMessage : INetMessage
    {
        public string  ObjectId;
        public bool    IsActive;
        public Vector3 Position;   // pour les portes : euler angles stockés ici

        public MsgId Id => MsgId.ObjectSync;

        public void Write(BinaryWriter w)
        { w.Write(ObjectId ?? string.Empty); w.Write(IsActive); w.Write(Position); }
        public void Read(BinaryReader r)
        { ObjectId = r.ReadString(); IsActive = r.ReadBoolean(); Position = r.ReadVector3(); }
    }

    // ── v1.5.0 — Lancement synchronisé de la partie ──────────────────────────
    // Émis par l'hôte quand il clique le bouton "Démarrer" dans le modal co-op.
    // Le guest, en recevant ce message, invoque programmatiquement le bouton
    // "Nouvelle Partie" / "New Game" de son menu MiSide pour démarrer la partie
    // au même instant que l'hôte. Pas de payload utile.
    public struct GameLaunchMessage : INetMessage
    {
        public MsgId Id => MsgId.GameLaunch;
        public void Write(BinaryWriter w) { w.Write((byte)0); }
        public void Read(BinaryReader r)  { r.ReadByte(); }
    }

    // ── v1.6.6 — POV partagée (séquence téléphone Mita) ──────────────────────
    // Quand un joueur ramasse le smartphone, il broadcast SharedPovMessage(Enable=true)
    // au peer. Le peer désactive sa MainCamera locale et crée une caméra spectateur
    // qui suit en temps réel la position+rotation tête de celui qui tient le téléphone
    // (déjà transportés par PlayerStateMessage v1.6.1). Quand le smartphone n'est
    // plus actif (rangé / fin séquence), le holder broadcast Enable=false.
    public struct SharedPovMessage : INetMessage
    {
        public bool   Enable;       // true = entre en POV partagée, false = sort
        public string OwnerName;    // nom du joueur qui tient l'item (informatif)

        public MsgId Id => MsgId.SharedPov;

        public void Write(BinaryWriter w)
        { w.Write(Enable); w.Write(OwnerName ?? string.Empty); }
        public void Read(BinaryReader r)
        { Enable = r.ReadBoolean(); OwnerName = r.ReadString(); }
    }

    // ── v1.6.6 — Forward d'input pendant la POV partagée ─────────────────────
    // Le peer (viewer) appuie sur E → on capture la touche et on l'envoie au
    // holder qui simule la pression locale (raycast → ObjectInteractive.Click).
    // KeyCode est sérialisé en string (ex: "E") pour rester extensible.
    public struct InputForwardMessage : INetMessage
    {
        public string KeyName;      // ex: "E", "Mouse0", "Space"
        public Vector3 OriginHint;  // optionnel : position du viewer (pour debug)

        public MsgId Id => MsgId.InputForward;

        public void Write(BinaryWriter w)
        { w.Write(KeyName ?? string.Empty); w.Write(OriginHint); }
        public void Read(BinaryReader r)
        { KeyName = r.ReadString(); OriginHint = r.ReadVector3(); }
    }
}

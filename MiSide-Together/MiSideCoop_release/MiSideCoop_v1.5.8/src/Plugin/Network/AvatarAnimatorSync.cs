using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiSideCoop.Network
{
    /// <summary>
    /// Applique les états d'animation reçus du réseau sur l'Animator du ghost.
    ///
    /// v1.5.7 — FIX D'ANIMATION DÉFINITIF :
    ///   Le dump IL2CPP du jeu (game_files/dump_output/stringliteral.json)
    ///   révèle les VRAIS noms des paramètres Animator MiSide. On les hardcode
    ///   ici et on les pousse via Animator.StringToHash + SetFloat(hash) /
    ///   SetBool(hash) — APIs IL2CPP-safe qui n'émettent pas de warnings
    ///   pour les params inexistants (contrairement aux versions string).
    ///
    /// Strategy summary :
    ///   1) PRIMARY : Animator.Play(fullPathHash, -1, normTime)  ← v1.5.0 (peut être stripé)
    ///   2) HARDCODED MIDDLE-PRIO : SetFloat("SpeedForward", v), SetBool("Walk"/"Run"/...)
    ///      ← v1.5.7, le vrai fix.
    ///   3) FALLBACK : English-name candidates (héritage v1.4.x)
    /// </summary>
    public class AvatarAnimatorSync : MonoBehaviour
    {
        /// <summary>v1.5.6 — Cache statique alimenté par RealMcCloner.</summary>
        public static class CachedAnimatorParams
        {
            public static readonly Dictionary<int, ParamSet> ByInstanceId = new Dictionary<int, ParamSet>();

            public class ParamSet
            {
                public readonly List<string> Floats   = new List<string>();
                public readonly List<string> Bools    = new List<string>();
                public readonly List<string> Triggers = new List<string>();
                public readonly List<string> Ints     = new List<string>();
            }

            public static ParamSet GetOrCreate(int instanceId)
            {
                if (!ByInstanceId.TryGetValue(instanceId, out var p))
                {
                    p = new ParamSet();
                    ByInstanceId[instanceId] = p;
                }
                return p;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // v1.5.7 — VRAIS NOMS DE PARAMS MiSide (extraits du dump IL2CPP)
        // ─────────────────────────────────────────────────────────────────────
        //
        // Source : game_files/dump_output/stringliteral.json
        // Méthode : grep des noms court PascalCase liés au mouvement / animation.
        // Ces noms sont utilisés par PlayerMove + Person_Animation MiSide
        // pour piloter les Animators du body / arms / mita.
        //
        // ATTENTION : tous ces noms ne sont PAS forcément des params Animator
        // (certains sont des state names, ou des fields C#). Mais SetFloat /
        // SetBool sur un nom inexistant produit juste un warning silencieux
        // (et avec StringToHash, même pas de warning). Donc on peut tous les
        // essayer sans risque.

        // Floats — pilotent les blend trees (idle ↔ walk ↔ run, look around, etc.)
        private static readonly string[] MiSideFloatParams =
        {
            "SpeedForward",        // ⭐ LE param principal — pilote le blend tree
            "MouseSpeed",
            "ShooterMouseSpeed",
            "HoldTime",
            "HeadMove",
            "KeyMove",
            "OtherAnimationAFloat",
            "OtherAnimationBFloat",
        };

        // Bools — gates des transitions (Idle→Walk, etc.)
        private static readonly string[] MiSideBoolParams =
        {
            "Move",
            "Walk",
            "Run",
            "Sit",
            "Fall",
            "Hide",
            "HandsUp",
            "Jump",
            "JumpStop",
            "StartRun",
            "StopRun",
            "BedSit",
            "KickSit",
            "Damage",
            "Idle",
            "IsRunic",
            "AnimationHold",
            "AnimationOther",
            "AnimationOtherA",
            "AnimationOtherB",
        };

        // Triggers — actions ponctuelles
        private static readonly string[] MiSideTriggerParams =
        {
            "Jump", "JumpStop", "Damage", "StartRun", "StopRun",
            "AnimationClipNext[A]", "AnimationClipNext[B]",
        };

        // Hashes pré-calculés (StringToHash N'EST PAS strippé en IL2CPP, c'est
        // une méthode static native qui marche partout).
        private static readonly int[] MiSideFloatHashes;
        private static readonly int[] MiSideBoolHashes;
        private static readonly int[] MiSideTriggerHashes;

        static AvatarAnimatorSync()
        {
            MiSideFloatHashes   = new int[MiSideFloatParams.Length];
            MiSideBoolHashes    = new int[MiSideBoolParams.Length];
            MiSideTriggerHashes = new int[MiSideTriggerParams.Length];
            for (int i = 0; i < MiSideFloatParams.Length; i++)
                MiSideFloatHashes[i] = Animator.StringToHash(MiSideFloatParams[i]);
            for (int i = 0; i < MiSideBoolParams.Length; i++)
                MiSideBoolHashes[i] = Animator.StringToHash(MiSideBoolParams[i]);
            for (int i = 0; i < MiSideTriggerParams.Length; i++)
                MiSideTriggerHashes[i] = Animator.StringToHash(MiSideTriggerParams[i]);
        }

        // Noms candidats anglais (fallback historique).
        private static readonly string[] SpeedCandidates   = { "Speed", "MoveSpeed", "Velocity", "BlendSpeed", "speed", "WalkSpeed", "InputMagnitude" };
        private static readonly string[] WalkingCandidates = { "IsWalking", "Walking", "isWalking", "isMoving", "IsMoving" };
        private static readonly string[] RunningCandidates = { "IsRunning", "Running", "isRunning", "isSprinting", "IsSprinting", "Sprint" };
        private static readonly string[] CrouchCandidates  = { "IsCrouching", "Crouching", "isCrouching", "Crouch" };

        private Animator _animator;
        private string   _currentState = "Idle";

        // Params découverts dynamiquement (s'ajoute aux hardcoded).
        private readonly HashSet<string> _dynamicSpeedParams   = new HashSet<string>();
        private readonly HashSet<string> _dynamicWalkingParams = new HashSet<string>();
        private readonly HashSet<string> _dynamicRunningParams = new HashSet<string>();
        private readonly HashSet<string> _triggerParams        = new HashSet<string>();

        public void Initialize(Animator animator)
        {
            _animator = animator;
            if (_animator == null) return;

            // v1.5.4 — Garanties Animator.
            try { _animator.cullingMode    = AnimatorCullingMode.AlwaysAnimate; } catch { }
            try { _animator.applyRootMotion = false; }                            catch { }
            try { _animator.enabled        = true; }                              catch { }

            // v1.5.6 — Tentative de lecture du cache statique alimenté par
            // RealMcCloner. Si présent et non-vide, on ajoute ces noms aux
            // hardcoded.
            int animId = 0;
            try { animId = _animator.GetInstanceID(); } catch { }
            if (animId != 0 && CachedAnimatorParams.ByInstanceId.TryGetValue(animId, out var cached))
            {
                foreach (var p in cached.Floats) _dynamicSpeedParams.Add(p);
                foreach (var p in cached.Bools)
                {
                    _dynamicWalkingParams.Add(p);
                    _dynamicRunningParams.Add(p);
                }
                foreach (var p in cached.Triggers) _triggerParams.Add(p);
            }

            MiSideCoopPlugin.Logger?.LogInfo(
                $"[Co-op] AnimatorSync.Initialize on '{_animator.gameObject.name}' (id={animId}): "
              + $"Hardcoded MiSide params ready ({MiSideFloatParams.Length} floats, "
              + $"{MiSideBoolParams.Length} bools, {MiSideTriggerParams.Length} triggers). "
              + $"Dynamic discovered: floats={_dynamicSpeedParams.Count}, "
              + $"bools={_dynamicWalkingParams.Count}, triggers={_triggerParams.Count}. "
              + "Using Animator.StringToHash for warning-free push.");
        }

        /// <summary>
        /// v1.5.7 — Pousse une vitesse de mouvement sur :
        ///   • Tous les params hardcoded MiSide (SpeedForward, MouseSpeed, ...).
        ///   • Tous les params découverts dynamiquement par RealMcCloner.
        ///   • Bool gates : Move/Walk/Run hardcoded + bool candidates dynamiques.
        ///
        /// Utilise StringToHash + SetFloat(hash) pour éviter les warnings
        /// "param 'XYZ' does not exist" qui spam le log Unity.
        /// </summary>
        public void SetMovementSpeed(float speed)
        {
            if (_animator == null) return;

            bool walking = speed > 0.05f;
            bool running = speed > 2.6f;
            bool moving  = walking;

            // ⭐ Hardcoded MiSide floats (push via hash → silent if param missing)
            try
            {
                for (int i = 0; i < MiSideFloatHashes.Length; i++)
                {
                    try { _animator.SetFloat(MiSideFloatHashes[i], speed); } catch { }
                }
            }
            catch { }

            // ⭐ Hardcoded MiSide bools — chaque nom reçoit une valeur cohérente.
            //    On utilise un switch par nom pour pousser la bonne logique
            //    (Walk = walking AND NOT running, Run = running, Move = walking, etc.)
            try
            {
                for (int i = 0; i < MiSideBoolParams.Length; i++)
                {
                    bool v;
                    switch (MiSideBoolParams[i])
                    {
                        case "Move":            v = moving;           break;
                        case "Walk":            v = walking && !running; break;
                        case "Run":             v = running;          break;
                        case "StartRun":        v = running;          break;
                        case "StopRun":         v = !running;         break;
                        case "Idle":            v = !moving;          break;
                        case "Sit":             v = false;            break;
                        case "Fall":            v = false;            break;
                        case "Hide":            v = false;            break;
                        case "HandsUp":         v = false;            break;
                        case "Jump":            v = false;            break;
                        case "JumpStop":        v = false;            break;
                        case "BedSit":          v = false;            break;
                        case "KickSit":         v = false;            break;
                        case "Damage":          v = false;            break;
                        case "IsRunic":         v = false;            break;
                        default:                v = moving;           break;
                    }
                    try { _animator.SetBool(MiSideBoolHashes[i], v); } catch { }
                }
            }
            catch { }

            // Dynamic params découverts (push tous, on ignore les erreurs).
            try
            {
                foreach (var p in _dynamicSpeedParams)
                {
                    try { _animator.SetFloat(p, speed); } catch { }
                }
                foreach (var p in _dynamicWalkingParams)
                {
                    try { _animator.SetBool(p, walking); } catch { }
                }
                foreach (var p in _dynamicRunningParams)
                {
                    try { _animator.SetBool(p, running); } catch { }
                }
            }
            catch { }

            // Fallback historique : candidats anglais (hash, silent).
            try
            {
                foreach (var c in SpeedCandidates)
                {
                    try { _animator.SetFloat(Animator.StringToHash(c), speed); } catch { }
                }
                foreach (var c in WalkingCandidates)
                {
                    try { _animator.SetBool(Animator.StringToHash(c), walking); } catch { }
                }
                foreach (var c in RunningCandidates)
                {
                    try { _animator.SetBool(Animator.StringToHash(c), running); } catch { }
                }
            }
            catch { }
        }

        public void SetAnimationState(string stateName, float speed)
        {
            if (_animator == null) return;
            SetMovementSpeed(speed);

            if (stateName == _currentState) return;
            _currentState = stateName;

            switch (stateName)
            {
                case "Interact":  FireTrigger("Interact");  FireTrigger("OnInteract");  break;
                case "PickUp":    FireTrigger("PickUp");    FireTrigger("OnPickup");    break;
                case "OpenDoor":  FireTrigger("OpenDoor");                              break;
                case "Die":       FireTrigger("Die");       FireTrigger("OnDeath");     FireTrigger("Damage"); break;
                case "Crouch":    FireTrigger("Crouch");    FireTrigger("Sit");         break;
                case "Jump":      FireTrigger("Jump");                                  break;
            }
        }

        public void PlayActionAnimation(string actionType) => SetAnimationState(actionType, 0f);

        private void FireTrigger(string triggerName)
        {
            if (_animator == null) return;
            try { _animator.SetTrigger(Animator.StringToHash(triggerName)); } catch { }
        }
    }
}

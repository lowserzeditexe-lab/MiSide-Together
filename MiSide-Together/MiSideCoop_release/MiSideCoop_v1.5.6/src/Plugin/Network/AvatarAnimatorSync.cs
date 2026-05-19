using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiSideCoop.Network
{
    /// <summary>
    /// Applique les états d'animation reçus du réseau sur l'Animator d'un avatar distant.
    ///
    /// v1.5.6 — STRATÉGIE À 3 NIVEAUX (en cascade) :
    ///   1) PRIMAIRE : Animator.Play(fullPathHash, -1, normTime)
    ///      → reproduit exactement l'animation jouée chez le peer (cf. v1.5.0).
    ///   2) SECONDAIRE (fallback movement-driven) : SetMovementSpeed(speed)
    ///      → pousse les params Speed/IsWalking/IsRunning découverts (cf. v1.5.4).
    ///   3) TERTIAIRE (legacy) : SetAnimationState pour les actions ponctuelles.
    ///
    /// v1.5.6 — DÉCOUVERTE STATIC-CONTEXT DES PARAMS
    ///   Le `parameterCount` + `GetParameter(int)` IL2CPP est stripé QUAND
    ///   appelé depuis une MonoBehaviour Il2Cpp-registered (= depuis ce script),
    ///   mais MARCHE depuis un helper static plain managed (= depuis
    ///   RealMcCloner.PrepareGhostAnimator). On exploite ça : RealMcCloner
    ///   énumère les params depuis un contexte qui fonctionne et les pousse
    ///   dans <see cref="CachedAnimatorParams"/>. AvatarAnimatorSync.Initialize
    ///   lit ce cache au lieu de retomber en best-effort aveugle.
    /// </summary>
    public class AvatarAnimatorSync : MonoBehaviour
    {
        /// <summary>
        /// v1.5.6 — Cache statique des paramètres énumérés depuis
        /// RealMcCloner.PrepareGhostAnimator (contexte static, IL2CPP-safe).
        /// Indexé par instanceId pour gérer plusieurs clones simultanés.
        /// </summary>
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

        private Animator _animator;
        private string   _currentState = "Idle";

        // Noms candidats anglais (fallback uniquement, peu probable que MiSide les utilise).
        private static readonly string[] SpeedCandidates    = { "Speed", "MoveSpeed", "Velocity", "BlendSpeed", "speed", "WalkSpeed", "InputMagnitude" };
        private static readonly string[] WalkingCandidates  = { "IsWalking", "Walking", "isWalking", "isMoving", "IsMoving" };
        private static readonly string[] RunningCandidates  = { "IsRunning", "Running", "isRunning", "isSprinting", "IsSprinting", "Sprint" };
        private static readonly string[] CrouchCandidates   = { "IsCrouching", "Crouching", "isCrouching", "Crouch" };

        // Params découverts (matchant les noms candidats anglais).
        private readonly HashSet<string> _speedParams     = new HashSet<string>();
        private readonly HashSet<string> _walkingParams   = new HashSet<string>();
        private readonly HashSet<string> _runningParams   = new HashSet<string>();
        private readonly HashSet<string> _crouchingParams = new HashSet<string>();
        private readonly HashSet<string> _triggerParams   = new HashSet<string>();

        // v1.5.4 — TOUS les float/bool params trouvés (fallback agnostique du nom).
        private readonly HashSet<string> _allFloatParams = new HashSet<string>();
        private readonly HashSet<string> _allBoolParams  = new HashSet<string>();

        private bool _bestEffortMode;

        public void Initialize(Animator animator)
        {
            _animator = animator;
            if (_animator == null) return;

            // v1.5.4 — Garanties Animator (cullingMode, applyRootMotion, enabled).
            try { _animator.cullingMode    = AnimatorCullingMode.AlwaysAnimate; } catch { }
            try { _animator.applyRootMotion = false; }                            catch { }
            try { _animator.enabled        = true; }                              catch { }

            // v1.5.6 — Lecture du cache statique alimenté par RealMcCloner.
            // Si on a des params en cache pour cet Animator (même instanceId),
            // on les utilise directement. C'est notre source la plus fiable car
            // l'énumération a réussi depuis un contexte non-stripé.
            int animId = 0;
            try { animId = _animator.GetInstanceID(); } catch { }
            CachedAnimatorParams.ParamSet cached = null;
            if (animId != 0 && CachedAnimatorParams.ByInstanceId.TryGetValue(animId, out cached))
            {
                foreach (var p in cached.Floats)
                {
                    _allFloatParams.Add(p);
                    foreach (var c in SpeedCandidates)
                        if (p == c) _speedParams.Add(p);
                }
                foreach (var p in cached.Bools)
                {
                    _allBoolParams.Add(p);
                    foreach (var c in WalkingCandidates)
                        if (p == c) _walkingParams.Add(p);
                    foreach (var c in RunningCandidates)
                        if (p == c) _runningParams.Add(p);
                    foreach (var c in CrouchCandidates)
                        if (p == c) _crouchingParams.Add(p);
                }
                foreach (var p in cached.Triggers) _triggerParams.Add(p);

                // Si aucun candidat anglais ne match (cas MiSide russe), on tape
                // sur TOUS les floats (un d'entre eux pilote forcément la blend
                // tree Idle→Walk→Run) et TOUS les bools (l'un d'eux gate la
                // transition). C'est large mais c'est ce qu'il faut pour ne
                // pas dépendre du nom des params.
                if (_speedParams.Count == 0 && _allFloatParams.Count > 0)
                {
                    foreach (var p in _allFloatParams) _speedParams.Add(p);
                }
                if (_walkingParams.Count == 0 && _allBoolParams.Count > 0)
                {
                    foreach (var p in _allBoolParams) _walkingParams.Add(p);
                }

                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] AnimatorSync.Initialize on '{_animator.gameObject.name}' (id={animId}): "
                  + $"using CACHED params from RealMcCloner — "
                  + $"floats=[{string.Join(",", _allFloatParams)}], "
                  + $"bools=[{string.Join(",", _allBoolParams)}], "
                  + $"triggers=[{string.Join(",", _triggerParams)}]. "
                  + $"Speed targets: {_speedParams.Count}, Walk gates: {_walkingParams.Count}.");
                return;
            }

            // Pas de cache → on essaie l'énumération directe (peut échouer en IL2CPP).
            int discoveredFloats = 0, discoveredBools = 0, discoveredTriggers = 0;
            try
            {
                int count = _animator.parameterCount;
                for (int i = 0; i < count; i++)
                {
                    var param = _animator.GetParameter(i);
                    if (param == null) continue;

                    switch (param.type)
                    {
                        case AnimatorControllerParameterType.Float:
                            _allFloatParams.Add(param.name);
                            discoveredFloats++;
                            foreach (var c in SpeedCandidates)
                                if (param.name == c) _speedParams.Add(param.name);
                            break;
                        case AnimatorControllerParameterType.Bool:
                            _allBoolParams.Add(param.name);
                            discoveredBools++;
                            foreach (var c in WalkingCandidates)
                                if (param.name == c) _walkingParams.Add(param.name);
                            foreach (var c in RunningCandidates)
                                if (param.name == c) _runningParams.Add(param.name);
                            foreach (var c in CrouchCandidates)
                                if (param.name == c) _crouchingParams.Add(param.name);
                            break;
                        case AnimatorControllerParameterType.Trigger:
                            _triggerParams.Add(param.name);
                            discoveredTriggers++;
                            break;
                    }
                }

                if (_speedParams.Count == 0 && _allFloatParams.Count > 0)
                {
                    foreach (var p in _allFloatParams) _speedParams.Add(p);
                }
                if (_walkingParams.Count == 0 && _allBoolParams.Count > 0)
                {
                    foreach (var p in _allBoolParams) _walkingParams.Add(p);
                }

                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] AnimatorSync.Initialize on '{_animator.gameObject.name}' (id={animId}): "
                  + $"direct enumeration OK — params={count} (floats={discoveredFloats}, "
                  + $"bools={discoveredBools}, triggers={discoveredTriggers}). "
                  + $"Speed targets: {_speedParams.Count}, Walk gates: {_walkingParams.Count}.");
            }
            catch (Exception ex)
            {
                _bestEffortMode = true;
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] AnimatorSync.Initialize: parameter enumeration stripped ({ex.Message}). "
                  + "No cache from RealMcCloner. Falling back to English-name candidates only.");
                foreach (var c in SpeedCandidates)    _speedParams.Add(c);
                foreach (var c in WalkingCandidates)  _walkingParams.Add(c);
                foreach (var c in RunningCandidates)  _runningParams.Add(c);
                foreach (var c in CrouchCandidates)   _crouchingParams.Add(c);
            }
        }

        /// <summary>
        /// v1.5.4+ — Pousse une vitesse calculée par delta-position sur TOUS
        /// les float candidates + bool walking/running candidates.
        /// </summary>
        public void SetMovementSpeed(float speed)
        {
            if (_animator == null) return;

            try
            {
                foreach (var p in _speedParams)
                {
                    try { _animator.SetFloat(p, speed); } catch { }
                }
            }
            catch { }

            bool walking = speed > 0.05f;
            bool running = speed > 2.6f;

            try
            {
                foreach (var p in _walkingParams)
                {
                    try { _animator.SetBool(p, walking); } catch { }
                }
                foreach (var p in _runningParams)
                {
                    try { _animator.SetBool(p, running); } catch { }
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

            bool walking   = stateName == "Walk" || stateName == "Run";
            bool running   = stateName == "Run";
            bool crouching = stateName == "Crouch";

            try
            {
                foreach (var p in _walkingParams)   { try { _animator.SetBool(p, walking); }   catch { } }
                foreach (var p in _runningParams)   { try { _animator.SetBool(p, running); }   catch { } }
                foreach (var p in _crouchingParams) { try { _animator.SetBool(p, crouching); } catch { } }
            }
            catch { }

            switch (stateName)
            {
                case "Interact":  FireTrigger("Interact");  FireTrigger("OnInteract");  break;
                case "PickUp":    FireTrigger("PickUp");    FireTrigger("OnPickup");    break;
                case "OpenDoor":  FireTrigger("OpenDoor");                              break;
                case "Die":       FireTrigger("Die");       FireTrigger("OnDeath");     break;
                case "Crouch":    FireTrigger("Crouch");                                break;
            }
        }

        public void PlayActionAnimation(string actionType) => SetAnimationState(actionType, 0f);

        private void FireTrigger(string triggerName)
        {
            if (_animator == null) return;
            try
            {
                if (_triggerParams.Contains(triggerName))
                {
                    _animator.SetTrigger(triggerName);
                }
                else if (_bestEffortMode)
                {
                    try { _animator.SetTrigger(triggerName); } catch { }
                }
            }
            catch { }
        }
    }
}

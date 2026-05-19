using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiSideCoop.Network
{
    /// <summary>
    /// Applique les états d'animation reçus du réseau sur l'Animator d'un avatar distant.
    ///
    /// v1.5.4 — Stratégie à 3 niveaux (en cascade) :
    ///   1) PRIMAIRE : Animator.Play(fullPathHash, -1, normTime)
    ///      → reproduit exactement l'animation jouée chez le peer.
    ///        Fait dans PlayerAvatar.ApplyRemoteState (cf. v1.5.0).
    ///   2) SECONDAIRE (fallback movement-driven) : SetMovementSpeed(speed)
    ///      → pousse les params Speed/IsWalking/IsRunning candidates.
    ///        Fait à chaque message reçu via la vitesse calculée par
    ///        delta-position dans PlayerAvatar (cf. v1.5.4).
    ///   3) TERTIAIRE (legacy v1.4.x) : SetAnimationState(state, speed)
    ///      → conservé pour compat des actions ponctuelles (Interact,
    ///        PickUp, OpenDoor, Die) qui passent par PlayActionAnimation.
    /// </summary>
    public class AvatarAnimatorSync : MonoBehaviour
    {

        private Animator _animator;
        private string   _currentState = "Idle";

        // Noms candidats pour les paramètres de l'Animator (MiSide + fallbacks communs)
        private static readonly string[] SpeedCandidates    = { "Speed", "MoveSpeed", "Velocity", "BlendSpeed", "speed", "WalkSpeed", "InputMagnitude" };
        private static readonly string[] WalkingCandidates  = { "IsWalking", "Walking", "isWalking", "isMoving", "IsMoving" };
        private static readonly string[] RunningCandidates  = { "IsRunning", "Running", "isRunning", "isSprinting", "IsSprinting", "Sprint" };
        private static readonly string[] CrouchCandidates   = { "IsCrouching", "Crouching", "isCrouching", "Crouch" };

        // Params découverts au runtime via l'introspection IL2CPP-safe.
        private readonly HashSet<string> _speedParams     = new HashSet<string>();
        private readonly HashSet<string> _walkingParams   = new HashSet<string>();
        private readonly HashSet<string> _runningParams   = new HashSet<string>();
        private readonly HashSet<string> _crouchingParams = new HashSet<string>();
        private readonly HashSet<string> _triggerParams   = new HashSet<string>();

        // v1.5.4 — TOUS les float params trouvés (pour le fallback movement-driven
        // quand les noms anglais ne matchent pas — typique des controllers MiSide
        // avec noms russes ou idiosyncratiques).
        private readonly HashSet<string> _allFloatParams = new HashSet<string>();
        private readonly HashSet<string> _allBoolParams  = new HashSet<string>();

        // Mode best-effort = on n'a pas pu énumérer les params, on tape en aveugle.
        private bool _bestEffortMode;

        public void Initialize(Animator animator)
        {
            _animator = animator;
            if (_animator == null) return;

            // v1.5.4 — Garanties de base sur l'Animator du ghost.
            //
            // (1) cullingMode = AlwaysAnimate : sans ça, Unity arrête d'updater
            //     l'Animator dès que le SkinnedMeshRenderer du clone est jugé
            //     hors champ. Or les bounds du SMR sont souvent invalides juste
            //     après Instantiate (bounding box pas refresh tant que les bones
            //     n'ont pas pris de pose) → animator gelé sur Entry → "pas d'anim".
            //
            // (2) applyRootMotion = false : sinon l'animator essaie de bouger
            //     transform.position via root motion → fight contre notre Lerp
            //     d'interpolation réseau dans PlayerAvatar.Update.
            //
            // (3) enabled = true : défensif au cas où un script désactiverait
            //     l'Animator au cours de l'init.
            try { _animator.cullingMode    = AnimatorCullingMode.AlwaysAnimate; } catch { }
            try { _animator.applyRootMotion = false; }                            catch { }
            try { _animator.enabled        = true; }                              catch { }

            // ─── Discovery des paramètres ─────────────────────────────────────
            // Animator.parameters (get_parameters) est STRIPPÉ dans le build
            // IL2CPP de MiSide. On essaie parameterCount + GetParameter(int)
            // qui sont prouvés moins souvent strippés.
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

                // v1.5.4 — Si l'énumération a marché mais qu'aucun de nos
                // candidats anglais n'a matché, on prend TOUS les float params
                // comme candidats Speed. Couvre les controllers MiSide avec
                // noms russes / cyrilliques / numérotés.
                if (_speedParams.Count == 0 && _allFloatParams.Count > 0)
                {
                    foreach (var p in _allFloatParams) _speedParams.Add(p);
                    MiSideCoopPlugin.Logger?.LogInfo(
                        $"[Co-op] AnimatorSync: no English-name Speed param matched, registering all "
                      + $"{_allFloatParams.Count} float param(s) as fallback speed targets: "
                      + $"[{string.Join(", ", _allFloatParams)}].");
                }

                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] AnimatorSync.Initialize on '{_animator.gameObject.name}': "
                  + $"controller='{(_animator.runtimeAnimatorController != null ? _animator.runtimeAnimatorController.name : "null")}', "
                  + $"layers={_animator.layerCount}, params={count} (floats={discoveredFloats}, bools={discoveredBools}, triggers={discoveredTriggers}). "
                  + $"Speed candidates matched: {_speedParams.Count}, Walking: {_walkingParams.Count}.");
            }
            catch (Exception ex)
            {
                _bestEffortMode = true;
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] AnimatorSync.Initialize: parameter enumeration stripped ({ex.Message}). " +
                    "Falling back to best-effort mode (all English candidates attempted, plus delta-position speed sync).");
                // Fallback : on inscrit tous les candidats anglais. SetFloat/SetBool sur
                // un paramètre absent du controller est un no-op (warning Unity au pire).
                foreach (var c in SpeedCandidates)    _speedParams.Add(c);
                foreach (var c in WalkingCandidates)  _walkingParams.Add(c);
                foreach (var c in RunningCandidates)  _runningParams.Add(c);
                foreach (var c in CrouchCandidates)   _crouchingParams.Add(c);
                // _triggerParams reste vide : sans nom de trigger précis on ne
                // peut pas appeler SetTrigger en aveugle (chaque trigger non
                // existant émet un warning, coûteux par frame).
            }
        }

        /// <summary>
        /// v1.5.4 — Pousse une vitesse de mouvement calculée à partir du delta
        /// position côté receveur. Sert de fallback robuste quand Animator.Play
        /// (hash) ne fait pas avancer le ghost (state inconnu côté clone,
        /// hash mismatch, etc.).
        ///
        /// Cette méthode est appelée chaque fois que PlayerAvatar reçoit un
        /// PlayerStateMessage. Elle pousse :
        ///   • SetFloat sur tous les Speed candidates → réveille la blend tree
        ///     marche/course du controller MiSide.
        ///   • SetBool sur tous les Walking / Running candidates pour les
        ///     state machines à transitions booléennes.
        /// </summary>
        public void SetMovementSpeed(float speed)
        {
            if (_animator == null) return;

            try
            {
                foreach (var p in _speedParams)
                {
                    try { _animator.SetFloat(p, speed); } catch { /* param absent : no-op */ }
                }
            }
            catch { }

            bool walking = speed > 0.05f;
            bool running = speed > 2.6f;     // course MiSide ~ 3.5 m/s

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

        /// <summary>
        /// Met à jour l'Animator de l'avatar distant selon l'état et la vitesse reçus.
        /// Conservé pour les actions ponctuelles (Interact / PickUp / OpenDoor / Die).
        /// </summary>
        public void SetAnimationState(string stateName, float speed)
        {
            if (_animator == null) return;
            // Pousser la vitesse à chaque appel (pas juste sur changement d'état).
            SetMovementSpeed(speed);

            if (stateName == _currentState) return;
            _currentState = stateName;

            // ── Paramètres booléens Marche / Course / Accroupi ────────────────
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

            // ── Actions ponctuelles (Triggers) ────────────────────────────────
            switch (stateName)
            {
                case "Interact":  FireTrigger("Interact");  FireTrigger("OnInteract");  break;
                case "PickUp":    FireTrigger("PickUp");    FireTrigger("OnPickup");    break;
                case "OpenDoor":  FireTrigger("OpenDoor");                              break;
                case "Die":       FireTrigger("Die");       FireTrigger("OnDeath");     break;
                case "Crouch":    FireTrigger("Crouch");                                break;
            }
        }

        /// <summary>Joue une animation d'action ponctuelle (appel depuis CoopNetworkManager).</summary>
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
                    // En best-effort, on tente le trigger : Unity log un warning si
                    // absent mais ne crashe pas. Coût marginal pour les actions
                    // ponctuelles (~1 par seconde max).
                    try { _animator.SetTrigger(triggerName); } catch { }
                }
            }
            catch { }
        }
    }
}

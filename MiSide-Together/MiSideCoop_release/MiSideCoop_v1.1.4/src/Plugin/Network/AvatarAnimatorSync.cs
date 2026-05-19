using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiSideCoop.Network
{
    /// <summary>
    /// Applique les états d'animation reçus du réseau sur l'Animator d'un avatar distant.
    /// Détecte automatiquement les paramètres disponibles dans l'AnimatorController
    /// pour éviter les exceptions en cas de noms différents selon la version du jeu.
    /// </summary>
    public class AvatarAnimatorSync : MonoBehaviour
    {

        private Animator _animator;
        private string   _currentState = "Idle";

        // Noms candidats pour les paramètres de l'Animator (MiSide + fallbacks communs)
        private static readonly string[] SpeedCandidates   = { "Speed", "MoveSpeed", "Velocity", "BlendSpeed", "speed" };
        private static readonly string[] WalkingCandidates = { "IsWalking", "Walking", "isWalking", "isMoving" };
        private static readonly string[] RunningCandidates = { "IsRunning", "Running", "isRunning", "isSprinting" };
        private static readonly string[] CrouchCandidates  = { "IsCrouching", "Crouching", "isCrouching", "Crouch" };

        private readonly HashSet<string> _speedParams    = new HashSet<string>();
        private readonly HashSet<string> _walkingParams  = new HashSet<string>();
        private readonly HashSet<string> _runningParams  = new HashSet<string>();
        private readonly HashSet<string> _crouchingParams = new HashSet<string>();
        private readonly HashSet<string> _triggerParams  = new HashSet<string>();

        public void Initialize(Animator animator)
        {
            _animator = animator;
            if (_animator == null) return;

            // ─── Discovery des paramètres ─────────────────────────────────────
            // Animator.parameters (get_parameters) est STRIPPÉ dans le build
            // IL2CPP de MiSide (constaté en runtime v1.1.3 :
            // "Method not found: 'UnityEngine.AnimatorControllerParameter[]
            //  UnityEngine.Animator.get_parameters()'").
            //
            // Stratégie : on essaie l'introspection ; si elle échoue, on bascule
            // en mode "best-effort" où on enregistre TOUS les candidats. Au
            // runtime, Animator.SetFloat/SetBool/SetTrigger sur un nom inexistant
            // logge un warning mais ne crashe pas — on accepte ce trade-off.
            try
            {
                foreach (var param in _animator.parameters)
                {
                    switch (param.type)
                    {
                        case AnimatorControllerParameterType.Float:
                            foreach (var c in SpeedCandidates)
                                if (param.name == c) _speedParams.Add(param.name);
                            break;
                        case AnimatorControllerParameterType.Bool:
                            foreach (var c in WalkingCandidates)
                                if (param.name == c) _walkingParams.Add(param.name);
                            foreach (var c in RunningCandidates)
                                if (param.name == c) _runningParams.Add(param.name);
                            foreach (var c in CrouchCandidates)
                                if (param.name == c) _crouchingParams.Add(param.name);
                            break;
                        case AnimatorControllerParameterType.Trigger:
                            _triggerParams.Add(param.name);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] AnimatorSync.Initialize : énumération paramètres strippée ({ex.Message}). " +
                    "Activation du mode best-effort (tous les candidats sont tentés).");
                // Fallback : on inscrit tous les candidats. SetFloat/SetBool sur
                // un paramètre absent dans le controller est un no-op (warning Unity).
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
        /// Met à jour l'Animator de l'avatar distant selon l'état et la vitesse reçus.
        /// </summary>
        public void SetAnimationState(string stateName, float speed)
        {
            if (_animator == null || stateName == _currentState) return;
            _currentState = stateName;

            // ── Paramètres de vitesse ─────────────────────────────────────────
            foreach (var p in _speedParams)
                _animator.SetFloat(p, speed);

            // ── Paramètres booléens Marche / Course / Accroupi ────────────────
            bool walking  = stateName == "Walk" || stateName == "Run";
            bool running  = stateName == "Run";
            bool crouching = stateName == "Crouch";

            foreach (var p in _walkingParams)  _animator.SetBool(p, walking);
            foreach (var p in _runningParams)  _animator.SetBool(p, running);
            foreach (var p in _crouchingParams) _animator.SetBool(p, crouching);

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
            if (_triggerParams.Contains(triggerName))
                _animator.SetTrigger(triggerName);
        }
    }
}

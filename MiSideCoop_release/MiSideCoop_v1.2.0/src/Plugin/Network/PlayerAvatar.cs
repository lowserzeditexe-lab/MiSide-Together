using System;
using UnityEngine;

namespace MiSideCoop.Network
{
    /// <summary>
    /// Classe de base pour tous les avatars (Joueur 1 et Joueur 2).
    /// Gère l'interpolation réseau position/rotation et délègue les animations
    /// à AvatarAnimatorSync.
    /// </summary>
    public abstract class PlayerAvatar : MonoBehaviour
    {

        // ── Propriétés publiques ──────────────────────────────────────────────
        public string PlayerName    { get; protected set; }
        public bool   IsLocalPlayer { get; protected set; }

        // ── Composants ────────────────────────────────────────────────────────
        protected Animator          _animator;
        protected AvatarAnimatorSync _animSync;

        // ── Interpolation réseau ──────────────────────────────────────────────
        protected Vector3    _targetPosition;
        protected Quaternion _targetRotation;
        private const float  LerpFactor = 15f;   // facteur d'interpolation

        // ── Initialisation ────────────────────────────────────────────────────
        public virtual void Initialize(string playerName, bool isLocal)
        {
            PlayerName    = playerName;
            IsLocalPlayer = isLocal;

            _animator  = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();
            _animSync  = GetComponent<AvatarAnimatorSync>() ?? gameObject.AddComponent<AvatarAnimatorSync>();
            _animSync.Initialize(_animator);

            _targetPosition = transform.position;
            _targetRotation = transform.rotation;
        }

        /// <summary>
        /// Applique un état reçu du réseau sur cet avatar distant.
        /// Ignoré si l'avatar appartient au joueur local.
        /// </summary>
        public void ApplyRemoteState(PlayerStateMessage msg)
        {
            if (IsLocalPlayer) return;

            _targetPosition = msg.Position;
            _targetRotation = msg.Rotation;
            _animSync?.SetAnimationState(msg.AnimationState, msg.MoveSpeed);
        }

        // ── Boucle Update ─────────────────────────────────────────────────────
        protected virtual void Update()
        {
            if (IsLocalPlayer) return;

            // Interpolation fluide vers la position/rotation cible
            transform.position = Vector3.Lerp(
                transform.position, _targetPosition, LerpFactor * Time.deltaTime);
            transform.rotation = Quaternion.Slerp(
                transform.rotation, _targetRotation, LerpFactor * Time.deltaTime);
        }
    }
}

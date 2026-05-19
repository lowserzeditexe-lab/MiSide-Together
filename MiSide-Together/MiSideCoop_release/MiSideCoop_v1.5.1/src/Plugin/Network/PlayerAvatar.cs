using System;
using UnityEngine;
using MiSideCoop.Avatars; // pour Il2CppComponentExtensions

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

            _animator  = GetComponent<Animator>() ?? this.GetComponentInChildrenSafe<Animator>();
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

            // v1.5.0 — Replay direct de l'AnimatorStateInfo source.
            //
            // Le clone 3D partage le MÊME AnimatorController que le MC local
            // → les state hashes sont identiques → Animator.Play(hash, layer, normTime)
            // reproduit exactement l'animation jouée chez le peer (idle, marche,
            // course, ramassage, cutscenes, dialogues, etc).
            //
            // Cette approche bypass complètement le système best-effort à base
            // de noms de paramètres (qui ne marchait pas car MiSide utilise des
            // noms russes que nos candidats anglais "Walk", "Run" ne couvraient
            // pas — d'où l'animation toujours bloquée en Idle).
            if (_animator != null && msg.AnimatorStateHash != 0)
            {
                try
                {
                    var currentInfo = _animator.GetCurrentAnimatorStateInfo(0);
                    // Évite de re-trigger Play() chaque frame sur le même state :
                    // ça réinitialiserait normalizedTime et créerait un tremblement.
                    // On ne Play que si on change d'état.
                    if (currentInfo.fullPathHash != msg.AnimatorStateHash)
                    {
                        _animator.Play(msg.AnimatorStateHash, 0, msg.AnimatorNormalizedTime);
                    }
                }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        $"[Co-op] PlayerAvatar: Animator.Play(hash={msg.AnimatorStateHash}) failed: {ex.Message}");
                }
            }
        }

        // ── Boucle Update ─────────────────────────────────────────────────────
        protected virtual void Update()
        {
            // v1.3.9 — Try/catch défensif autour de Update. Au changement
            // de scène, certaines références (transform, _animator) peuvent
            // devenir des Unity-destroyed objects, ce qui levait des
            // NullReferenceException silencieuses côté guest et faisait
            // tomber la pump TCP (= disconnect immédiat dès que host change
            // de scène). Avec DontDestroyOnLoad sur le GameObject + ce
            // catch, on tolère une frame transitoire sans crasher la
            // session co-op.
            try
            {
                if (IsLocalPlayer) return;

                // Interpolation fluide vers la position/rotation cible
                transform.position = Vector3.Lerp(
                    transform.position, _targetPosition, LerpFactor * Time.deltaTime);
                transform.rotation = Quaternion.Slerp(
                    transform.rotation, _targetRotation, LerpFactor * Time.deltaTime);
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] PlayerAvatar.Update transient error: {ex.Message}");
            }
        }
    }
}

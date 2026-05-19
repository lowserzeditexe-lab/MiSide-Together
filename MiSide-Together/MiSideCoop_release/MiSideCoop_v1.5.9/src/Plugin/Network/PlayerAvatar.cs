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

        // v1.5.4 — Vitesse de mouvement estimée à partir du delta-position
        // entre messages réseau. Sert de fallback pour faire jouer les anims
        // de marche/course quand Animator.Play(hash) ne déclenche pas la
        // transition sur le clone.
        private float   _lastRemoteStateTime;
        private Vector3 _lastRemotePosition;
        private float   _estimatedSpeed;
        private bool    _hasPrevRemoteSample;

        // v1.5.4 — Diagnostic : on log une fois si Play(hash) lance une exception.
        private bool _playWarningEmitted;

        // ── Initialisation ────────────────────────────────────────────────────
        public virtual void Initialize(string playerName, bool isLocal)
        {
            PlayerName    = playerName;
            IsLocalPlayer = isLocal;

            _animator  = GetComponent<Animator>() ?? this.GetComponentInChildrenSafe<Animator>();
            _animSync  = GetComponent<AvatarAnimatorSync>() ?? gameObject.AddComponent<AvatarAnimatorSync>();

            // v1.5.4 — Garanties Animator AVANT l'init de AvatarAnimatorSync,
            // qui réapplique aussi ces réglages par sécurité. Évite que le
            // ghost se fige sur l'état Entry par un cullingMode trop strict
            // ou par root motion qui fight contre notre Lerp.
            if (_animator != null && !isLocal)
            {
                try { _animator.cullingMode    = AnimatorCullingMode.AlwaysAnimate; } catch { }
                try { _animator.applyRootMotion = false; }                            catch { }
                try { _animator.enabled        = true; }                              catch { }
            }

            _animSync.Initialize(_animator);

            _targetPosition = transform.position;
            _targetRotation = transform.rotation;

            _lastRemoteStateTime = Time.time;
            _lastRemotePosition  = transform.position;
            _hasPrevRemoteSample = false;
            _estimatedSpeed      = 0f;
        }

        // v1.5.8 - Diagnostic counters (throttled log to avoid spam).
        private int   _msgCount;
        private float _peakSpeedSeen;

        /// <summary>
        /// Applique un état reçu du réseau sur cet avatar distant.
        /// Ignoré si l'avatar appartient au joueur local.
        /// </summary>
        public void ApplyRemoteState(PlayerStateMessage msg)
        {
            if (IsLocalPlayer) return;

            _targetPosition = msg.Position;
            _targetRotation = msg.Rotation;

            // v1.5.4 — CALCUL DE VITESSE DEPUIS LE DELTA-POSITION
            //
            // On évite de faire confiance à msg.MoveSpeed (qui peut être 0 si le
            // sampling Animator.GetFloat("Speed") échoue côté envoyeur — par ex.
            // si MiSide utilise un nom de param non-anglais). À la place on
            // calcule la vitesse à partir de la distance parcourue entre deux
            // messages — méthode robuste, indépendante du nom des params.
            float now = Time.time;
            float dt  = now - _lastRemoteStateTime;
            if (_hasPrevRemoteSample && dt > 0.005f)
            {
                float dist     = Vector3.Distance(msg.Position, _lastRemotePosition);
                float instantV = dist / dt;
                // Lissage exponentiel pour absorber le jitter réseau.
                _estimatedSpeed = Mathf.Lerp(_estimatedSpeed, instantV, 0.4f);
            }
            _lastRemoteStateTime = now;
            _lastRemotePosition  = msg.Position;
            _hasPrevRemoteSample = true;

            // Préfère la vitesse calculée (toujours fiable) sur celle reçue
            // (souvent 0 sur MiSide à cause des params non-anglais).
            float speedForAnim = _estimatedSpeed > msg.MoveSpeed ? _estimatedSpeed : msg.MoveSpeed;
            _animSync?.SetMovementSpeed(speedForAnim);

            // Compat legacy v1.4.x — états textuels (Interact/PickUp/etc.).
            // Ne fait que poser des triggers si on connaît leur nom.
            if (!string.IsNullOrEmpty(msg.AnimationState))
                _animSync?.SetAnimationState(msg.AnimationState, speedForAnim);

            // v1.5.0 + v1.5.4 — Replay direct de l'AnimatorStateInfo source.
            //
            // Le clone 3D partage le MÊME AnimatorController que le MC local
            // → les state hashes sont identiques → Animator.Play(hash, -1, normTime)
            // reproduit exactement l'animation jouée chez le peer (idle, marche,
            // course, ramassage, cutscenes, dialogues, etc).
            //
            // v1.5.4 — IMPORTANT : on passe layer = -1 au lieu de layer = 0.
            // Unity utilise alors le layer dans lequel le state existe
            // réellement (et il n'y en a qu'un puisque fullPathHash est unique
            // dans le controller). Avec layer=0 forcé, si MiSide définit
            // l'état dans un Upper/LowerBody layer, Play() faisait un no-op
            // silencieux → ghost figé.
            if (_animator != null && msg.AnimatorStateHash != 0)
            {
                try
                {
                    var currentInfo = _animator.GetCurrentAnimatorStateInfo(0);
                    if (currentInfo.fullPathHash != msg.AnimatorStateHash)
                    {
                        _animator.Play(msg.AnimatorStateHash, -1, msg.AnimatorNormalizedTime);
                    }
                }
                catch (Exception ex)
                {
                    if (!_playWarningEmitted)
                    {
                        _playWarningEmitted = true;
                        MiSideCoopPlugin.Logger?.LogWarning(
                            $"[Co-op] PlayerAvatar: Animator.Play(hash={msg.AnimatorStateHash}) failed: "
                          + $"{ex.Message}. Falling back to movement-driven anim only (further failures suppressed).");
                    }
                }
            }

            // v1.5.8 - Throttled diagnostic log so we can see what the
            // animation pipeline is actually doing. Logs once every ~60
            // messages (= ~2s at 30Hz network tick).
            _msgCount++;
            if (_estimatedSpeed > _peakSpeedSeen) _peakSpeedSeen = _estimatedSpeed;
            if ((_msgCount % 60) == 1)
            {
                int curHash = 0;
                float curNorm = 0f;
                try
                {
                    if (_animator != null)
                    {
                        var info = _animator.GetCurrentAnimatorStateInfo(0);
                        curHash = info.fullPathHash;
                        curNorm = info.normalizedTime;
                    }
                }
                catch { }
                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] anim-diag '{PlayerName}' msg#{_msgCount}: "
                  + $"recvSpeed={msg.MoveSpeed:F2} estSpeed={_estimatedSpeed:F2} peak={_peakSpeedSeen:F2} "
                  + $"recvHash={msg.AnimatorStateHash} recvNT={msg.AnimatorNormalizedTime:F2} "
                  + $"localHash={curHash} localNT={curNorm:F2} "
                  + $"state='{msg.AnimationState}'");
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

                // v1.5.4 — Décay de la vitesse estimée quand on ne reçoit plus
                // de messages (peer immobile). Sans ça, le ghost continue à
                // jouer la walk anim indéfiniment après que le peer s'arrête.
                float idleAge = Time.time - _lastRemoteStateTime;
                if (idleAge > 0.15f && _estimatedSpeed > 0.01f)
                {
                    _estimatedSpeed = Mathf.Lerp(_estimatedSpeed, 0f, 8f * Time.deltaTime);
                    _animSync?.SetMovementSpeed(_estimatedSpeed);
                }
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] PlayerAvatar.Update transient error: {ex.Message}");
            }
        }
    }
}

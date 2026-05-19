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
        // v1.6.1 — Head bone cache (résolu une fois à l'init).
        protected Transform         _headBone;
        // v1.6.1 — Buffer de la rotation head bone reçue. Appliqué après
        // Animator.Update via LateUpdate pour overrider toute anim qui
        // toucherait l'os Head.
        private Quaternion _remoteHeadRotation = Quaternion.identity;
        private bool       _hasRemoteHeadRot;

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

            // v1.6.0 — Résolution robuste de l'Animator : préférer celui qui
            // a un runtimeAnimatorController valide ET parameterCount > 0.
            // Sur le ghost (clone 'Person'), il y a souvent 2 Animators :
            //   • Animator sur root 'Player2_Guest' (controller='', invalide)
            //   • Animator sur un enfant (Hips/Person/...) avec le vrai
            //     controller MiSide
            // Sans ce filtre, GetComponent<Animator>() prend le 1er trouvé
            // (= invalide) → Animator.Play(hash) no-op, ghost reste en T-pose.
            _animator = ResolveBestAnimator(gameObject);
            _animSync  = GetComponent<AvatarAnimatorSync>() ?? gameObject.AddComponent<AvatarAnimatorSync>();

            // v1.6.3 — Log explicite de l'Animator sélectionné (pour diagnostiquer
            // les bugs où le ghost reste en T-pose / sans animation).
            if (!isLocal)
            {
                try
                {
                    if (_animator != null)
                    {
                        string anName = "?", ctrlName = "?";
                        int pc = -1;
                        try { anName   = _animator.gameObject.name; } catch { }
                        try { ctrlName = _animator.runtimeAnimatorController?.name ?? "<null>"; } catch { }
                        try { pc       = _animator.parameterCount; } catch { }
                        MiSideCoopPlugin.Logger?.LogInfo(
                            $"[Co-op] PlayerAvatar.Initialize remote '{playerName}': "
                          + $"animator on GO='{anName}', controller='{ctrlName}', params={pc}.");
                    }
                    else
                    {
                        MiSideCoopPlugin.Logger?.LogWarning(
                            $"[Co-op] PlayerAvatar.Initialize remote '{playerName}': "
                          + "NO animator resolved on ghost → animations will not play.");
                    }
                }
                catch { }
            }

            // v1.6.1 — Résolution du head bone (utilisé pour appliquer
            // HeadRotation reçue sur LateUpdate, après Animator.Update).
            _headBone = ResolveHeadBoneInHierarchy(gameObject);

            // v1.5.4 — Garanties Animator AVANT l'init de AvatarAnimatorSync,
            // qui réapplique aussi ces réglages par sécurité. Évite que le
            // ghost se fige sur l'état Entry par un cullingMode trop strict
            // ou par root motion qui fight contre notre Lerp.
            if (_animator != null && !isLocal)
            {
                try { _animator.cullingMode    = AnimatorCullingMode.AlwaysAnimate; } catch { }
                try { _animator.applyRootMotion = false; }                            catch { }
                try { _animator.enabled        = true; }                              catch { }
                // v1.6.0 — Rebind + Update(0) pour sortir le clone de la T-pose
                // initiale. Sans ce kick, le ghost peut rester en bind pose
                // jusqu'à ce qu'un trigger explicite ré-évalue le controller.
                try { _animator.Rebind();    } catch { }
                try { _animator.Update(0f); } catch { }
            }

            _animSync.Initialize(_animator);

            _targetPosition = transform.position;
            _targetRotation = transform.rotation;

            _lastRemoteStateTime = Time.time;
            _lastRemotePosition  = transform.position;
            _hasPrevRemoteSample = false;
            _estimatedSpeed      = 0f;
        }

        // v1.6.0 — Choisit le meilleur Animator dans la hiérarchie :
        // celui qui a un controller assigné ET au moins 1 paramètre.
        // Préférence aux GO nommés 'Person*'. Fallback sur le 1er Animator.
        //
        // v1.6.3 — Reconnait aussi les noms de root clonés (Player2_Guest,
        // Player1_Host_Remote) comme priorité, car RealMcCloner renomme le
        // GO racine ('Person' → 'Player2_Guest' / 'Player1_Host_Remote')
        // après Instantiate. Sans cet ajout, ResolveBestAnimator pouvait
        // tomber sur un Animator enfant accessoire au lieu du body Animator
        // racine porteur du blend tree de mouvement → animations cassées.
        private static Animator ResolveBestAnimator(GameObject root)
        {
            if (root == null) return null;
            try
            {
                var all = root.transform.GetComponentsInChildrenSafe<Animator>(true);
                Animator best = null;
                foreach (var a in all)
                {
                    if (a == null) continue;
                    bool hasCtrl = false;
                    int  pc      = 0;
                    try { hasCtrl = a.runtimeAnimatorController != null; } catch { }
                    try { pc      = a.parameterCount; }                    catch { }
                    if (hasCtrl && pc > 0)
                    {
                        string goName = string.Empty;
                        try { goName = a.gameObject.name; } catch { }
                        // v1.6.3 — Priorité étendue : 'Person*' ET noms de root clonés.
                        if (!string.IsNullOrEmpty(goName) &&
                            (goName.StartsWith("Person")
                             || goName == "Player2_Guest"
                             || goName == "Player1_Host_Remote"))
                            return a;
                        if (best == null) best = a;
                    }
                }
                if (best != null) return best;
                // Fallback : 1er Animator quel que soit son état
                foreach (var a in all)
                {
                    if (a != null) return a;
                }
            }
            catch { }
            try { return root.GetComponent<Animator>(); } catch { return null; }
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

            // v1.6.0 — APPLIQUE LES VRAIS PARAMS NATIFS MiSide sur le ghost.
            // 'Forward' et 'Right' sont les floats que MiSide pousse en local
            // sur l'Animator 'Person' (confirmé par AnimatorDiagPatch sur le
            // log v1.5.9 : "anim-patch SetFloat on='Person' param='Forward'
            // value=-0.197"). Sans ces floats, le blend tree du body 3rd-person
            // reste figé sur l'état Idle → ghost en T-pose / posture immobile
            // même en se déplaçant en world space.
            //
            // Push string-based (et non hash) pour s'assurer que le binding
            // côté Unity match exactement le param du controller cloné. Si la
            // sender n'a pas pu sampler (anciens clients < v1.6.0), MoveForward
            // = 0 → fallback sur estimatedSpeed via SetMovementSpeed ci-dessus.
            //
            // v1.6.3 — Push TOUJOURS Forward/Right (même à 0/0), pour assurer
            // un retour fluide à Idle quand le peer s'arrête. Sans push à 0,
            // le blend tree restait coincé sur la dernière valeur reçue >0.
            if (_animator != null)
            {
                if (msg.MoveForward != 0f || msg.MoveRight != 0f)
                {
                    try { _animator.SetFloat("Forward", msg.MoveForward); } catch { }
                    try { _animator.SetFloat("Right",   msg.MoveRight);   } catch { }
                }
                else if (speedForAnim > 0.05f)
                {
                    // Fallback v1.5.x : on n'a pas de signe, on suppose avant.
                    try { _animator.SetFloat("Forward", speedForAnim); } catch { }
                    try { _animator.SetFloat("Right",   0f);           } catch { }
                }
                else
                {
                    try { _animator.SetFloat("Forward", 0f); } catch { }
                    try { _animator.SetFloat("Right",   0f); } catch { }
                }
            }

            // v1.6.1 — Sit / crouch bool. Confirmé par AnimatorDiagPatch :
            // c'est l'unique bool MiSide pour le crouch / la pose assise.
            if (_animator != null)
            {
                try { _animator.SetBool("Sit", msg.IsCrouching); } catch { }
            }

            // v1.6.1 — Buffer la rotation du head bone. On l'applique en
            // LateUpdate APRÈS Animator.Update pour écraser toute anim qui
            // toucherait au head bone. En MiSide, le head suit le mouse-look
            // directement (rotation appliquée hors Animator), donc Animator.Play
            // ne reproduit pas l'inclinaison. Sans ce push direct, la tête du
            // ghost reste droit devant même si le peer regarde haut/bas/côtés.
            //
            // On stocke (au lieu d'appliquer directement) pour respecter
            // l'ordre d'exécution Unity : Update → Animator → LateUpdate.
            // SetFloat/SetBool dans Update est appliqué par l'Animator
            // automatiquement ; SetLocalRotation d'un bone doit être fait
            // APRÈS l'Animator sinon l'anim courante l'écrase à chaque frame.
            _remoteHeadRotation = msg.HeadRotation;
            _hasRemoteHeadRot   = true;

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

            // v1.6.3 — Force l'évaluation du controller dans la frame courante.
            // Sans Animator.Update(0), les SetFloat/SetBool/Play appliqués dans
            // ApplyRemoteState (appelé hors de la phase Unity Update) ne sont
            // pris en compte qu'à la frame suivante. Au taux 20 Hz du réseau,
            // cela introduisait un délai visible d'animation de 50–100 ms et,
            // pire, manquait parfois la transition quand un nouvel état était
            // déjà passé. Update(0f) tick l'Animator sans avancer le temps,
            // ce qui évalue le blend tree avec les nouveaux params immédiatement.
            if (_animator != null)
            {
                try { _animator.Update(0f); } catch { }
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
                  + $"recvFwd={msg.MoveForward:F2} recvRight={msg.MoveRight:F2} "
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

        // ── LateUpdate v1.6.1 ────────────────────────────────────────────────
        // Écrase la rotation du head bone APRÈS l'Animator.Update. Sans ça,
        // l'anim courante remettrait le head bone à sa valeur du moment chaque
        // frame, et le mouse-look du peer ne se verrait jamais sur le ghost.
        protected virtual void LateUpdate()
        {
            try
            {
                if (IsLocalPlayer) return;
                if (!_hasRemoteHeadRot) return;
                if (_headBone == null) return;
                _headBone.localRotation = _remoteHeadRotation;
            }
            catch { /* head bone may be destroyed between scenes */ }
        }

        // v1.6.1 — Résolution du head bone, identique à celle du sender.
        // Compatible MiSide / Unity Humanoid / Mixamo. Cherche d'abord par
        // match exact, puis par suffixe approximatif (évitant "headphones").
        private static readonly string[] HeadBoneCandidates =
        {
            "Head", "head", "HeadCC", "Head_M", "HeadBone",
            "mixamorig:Head", "mixamorig:head",
            "Bip01 Head", "Bip01_Head",
            "Player Head", "PlayerHead",
        };
        private static Transform ResolveHeadBoneInHierarchy(GameObject root)
        {
            if (root == null) return null;
            try
            {
                var transforms = root.transform.GetComponentsInChildrenSafe<Transform>(true);
                foreach (var t in transforms)
                {
                    if (t == null) continue;
                    string n = null;
                    try { n = t.gameObject.name; } catch { }
                    if (string.IsNullOrEmpty(n)) continue;
                    foreach (var cand in HeadBoneCandidates)
                        if (n == cand) return t;
                }
                foreach (var t in transforms)
                {
                    if (t == null) continue;
                    string n = null;
                    try { n = t.gameObject.name; } catch { }
                    if (string.IsNullOrEmpty(n)) continue;
                    var lo = n.ToLowerInvariant();
                    if ((lo == "head" || lo.EndsWith(":head") || lo.EndsWith("_head") || lo.EndsWith(" head"))
                        && !lo.Contains("phone") && !lo.Contains("set"))
                        return t;
                }
            }
            catch { }
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using MiSideCoop.Avatars;
using MiSideCoop.Patches;

namespace MiSideCoop.Network
{
    /// <summary>
    /// v1.6.6 — Mode "POV partagée".
    ///
    /// Quand un joueur ramasse le smartphone (séquence Mita-dans-le-téléphone),
    /// l'autre joueur entre automatiquement dans un mode où sa caméra suit en
    /// temps réel la position + rotation tête du holder. Du coup les deux
    /// joueurs vivent exactement la même cutscene Mita sur leur écran.
    ///
    /// Côté HOLDER (celui qui tient le téléphone) :
    ///   • Son jeu tourne normalement, sans modification visuelle.
    ///   • Il écoute les <see cref="InputForwardMessage"/> envoyés par le viewer
    ///     pour répliquer ses pressions E (= comme si les 2 contrôlaient le même MC).
    ///   • Il monitore <c>Player/RightItem FixPosition/Smartphone.activeInHierarchy</c>
    ///     et broadcast <see cref="SharedPovMessage"/>(Enable=false) dès que le
    ///     téléphone est rangé.
    ///
    /// Côté VIEWER (le peer) :
    ///   • Désactive sa propre MainCamera, crée une CoopSpectatorCamera qui
    ///     interpole sur les PlayerStateMessage reçus du holder.
    ///   • Bloque ses inputs de mouvement (figé sur place).
    ///   • Cache le clone 3D du peer (sinon visible au milieu de l'écran).
    ///   • F7 = sortie manuelle forcée (au cas où ça bug).
    ///   • Pression E → broadcast <see cref="InputForwardMessage"/>(KeyName="E").
    ///
    /// Sans overlay UI : transition seamless comme demandé.
    /// </summary>
    public class SharedPovController : MonoBehaviour
    {
        public static SharedPovController Instance { get; private set; }

        // ── Constants ─────────────────────────────────────────────────────────
        private const string SmartphonePath = "Player/RightItem FixPosition/Smartphone";
        private const float SmartphoneLostTimeout = 1.5f; // sec sans Smartphone actif avant auto-exit
        private const KeyCode ManualExitKey = KeyCode.F7;
        private const float CamLerpSpeed = 25f; // interpolation lissage caméra spectateur

        // ── État global ───────────────────────────────────────────────────────
        public bool IsActive { get; private set; }
        public bool IsViewer { get; private set; }
        public bool IsHolder { get; private set; }
        public string OwnerName { get; private set; } = "...";

        // ── État côté HOLDER ──────────────────────────────────────────────────
        private GameObject _smartphone;
        private float _smartphoneLostTimer;

        // ── État côté VIEWER ──────────────────────────────────────────────────
        private Camera _myMainCam;
        private bool _myMainCamWasEnabled;
        private GameObject _spectatorCamGo;
        private Camera _spectatorCam;
        private AudioListener _spectatorAudio;
        private GameObject _hiddenGhost;
        private bool _hiddenGhostWasActive;

        // Position cible reçue du holder (interpolée chaque frame)
        private Vector3 _targetHeadPos;
        private Quaternion _targetHeadRot;
        private bool _hasTarget;

        // Throttle pour le forward E
        private float _lastForwardTime;
        private const float ForwardCooldown = 0.2f;

        // ── v1.6.7 — Monitoring direct du Smartphone (côté host uniquement) ──
        // MiSide active le Smartphone via une cutscene scriptée (pas via
        // PlayerMove.TakeItem), donc notre hook PickupPatch ne se déclenche
        // pas pour la séquence Mita. On surveille directement
        // Player/RightItem FixPosition/Smartphone.activeInHierarchy
        // et on déclenche StartAsHolder dès qu'il passe inactive→active.
        // Convention : seul le HOST déclenche → évite les race conditions
        // si la cutscene s'exécute simultanément sur les 2 PC.
        private GameObject _watchedSmartphone;
        private bool _watchedSmartphoneWasActive;
        private float _watchedSmartphoneScanTimer;
        private const float WatchedScanInterval = 2f;

        // ── Lifecycle ─────────────────────────────────────────────────────────
        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ── API publique appelée depuis PickupPatch / CoopNetworkManager ─────

        /// <summary>
        /// Appelé localement quand on ramasse soi-même le smartphone.
        /// On devient le HOLDER et on broadcast l'entrée de POV partagée au peer.
        /// </summary>
        public void StartAsHolder()
        {
            if (IsActive)
            {
                // Déjà en mode → ne rien faire (probablement déjà holder)
                return;
            }
            IsActive = true;
            IsHolder = true;
            IsViewer = false;
            OwnerName = MiSideCoopPlugin.LocalPlayerName.Value;

            // Localise le smartphone pour pouvoir monitorer activeInHierarchy
            _smartphone = SafeFind(SmartphonePath);
            _smartphoneLostTimer = 0f;

            // Broadcast au peer
            CoopNetworkManager.Instance?.BroadcastSharedPov(true, OwnerName);
            MiSideCoopPlugin.Logger?.LogInfo(
                $"[SharedPov] HOLDER mode entered (owner='{OwnerName}', smartphone={(_smartphone != null ? "tracked" : "not-found")}).");
        }

        /// <summary>
        /// Appelé sur réception d'un SharedPovMessage(Enable=true) côté peer.
        /// On devient le VIEWER : désactive notre caméra, créée la spectator cam.
        /// </summary>
        public void StartAsViewer(string ownerName)
        {
            if (IsActive)
            {
                // Déjà en mode → on update juste le nom
                OwnerName = ownerName ?? OwnerName;
                return;
            }
            IsActive = true;
            IsViewer = true;
            IsHolder = false;
            OwnerName = string.IsNullOrEmpty(ownerName) ? "Peer" : ownerName;

            try { SetupSpectatorCamera(); }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogError($"[SharedPov] SetupSpectatorCamera failed: {ex.Message}");
                Exit();
                return;
            }

            try { HideRemoteGhost(); }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning($"[SharedPov] HideRemoteGhost failed: {ex.Message}");
            }

            MiSideCoopPlugin.Logger?.LogInfo(
                $"[SharedPov] VIEWER mode entered (watching '{OwnerName}'). Press F7 to exit.");
        }

        /// <summary>
        /// Sortie propre (côté holder ou viewer). Restaure tout, broadcast la fin
        /// si on est le holder, et reset l'état.
        /// </summary>
        public void Exit()
        {
            if (!IsActive) return;

            bool wasHolder = IsHolder;
            bool wasViewer = IsViewer;
            IsActive = false;
            IsHolder = false;
            IsViewer = false;

            if (wasHolder)
            {
                // Broadcast la fin au peer
                CoopNetworkManager.Instance?.BroadcastSharedPov(false, OwnerName);
                _smartphone = null;
                _smartphoneLostTimer = 0f;
                MiSideCoopPlugin.Logger?.LogInfo("[SharedPov] HOLDER mode exited.");
            }

            if (wasViewer)
            {
                try { TeardownSpectatorCamera(); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogWarning($"[SharedPov] TeardownSpectatorCamera failed: {ex.Message}");
                }
                try { RestoreRemoteGhost(); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogWarning($"[SharedPov] RestoreRemoteGhost failed: {ex.Message}");
                }
                _hasTarget = false;
                MiSideCoopPlugin.Logger?.LogInfo("[SharedPov] VIEWER mode exited.");
            }

            OwnerName = "...";
        }

        /// <summary>
        /// Reçoit la position+rotation tête du holder via PlayerStateMessage
        /// pour piloter la caméra spectateur.
        /// </summary>
        public void OnRemoteState(Vector3 holderPos, Quaternion holderRot, Quaternion headRot)
        {
            if (!IsActive || !IsViewer) return;

            // On vise la position de la tête (≈ +1.65m au-dessus du root du Player).
            // L'os 'Head' du MC est à ~1.65m du sol — bonne approximation.
            // On utilise holderRot (rotation du Player root) combinée à headRot
            // (rotation locale de l'os Head) pour reproduire le mouse-look exact.
            _targetHeadPos = holderPos + Vector3.up * 1.65f;
            _targetHeadRot = holderRot * headRot;
            _hasTarget = true;
        }

        /// <summary>
        /// Reçoit un InputForwardMessage envoyé par le viewer.
        /// Côté holder : on simule la pression du peer (E = trigger interact).
        /// </summary>
        public void OnRemoteInput(string keyName)
        {
            if (!IsActive || !IsHolder) return;
            if (string.IsNullOrEmpty(keyName)) return;

            if (keyName == "E")
            {
                TryTriggerLocalInteract();
            }
            // Extensible : autres touches futures (clic souris, etc.)
        }

        // ── Update (logique tick) ─────────────────────────────────────────────
        private void Update()
        {
            // v1.6.7 — Monitoring direct de l'activation du Smartphone côté host.
            // MiSide active l'objet via cutscene scriptée, pas via TakeItem(), donc
            // le hook PickupPatch ne se déclenche pas pour cette séquence. On poll
            // l'état activeInHierarchy ici pour basculer en HOLDER auto.
            TickSmartphoneWatcher();

            if (!IsActive) return;

            // HOLDER : surveille la disparition du smartphone pour auto-exit
            if (IsHolder)
            {
                bool stillActive = false;
                try
                {
                    if (_smartphone == null)
                        _smartphone = SafeFind(SmartphonePath);
                    if (_smartphone != null)
                        stillActive = _smartphone.activeInHierarchy;
                }
                catch { }

                if (!stillActive)
                {
                    _smartphoneLostTimer += Time.deltaTime;
                    if (_smartphoneLostTimer >= SmartphoneLostTimeout)
                    {
                        MiSideCoopPlugin.Logger?.LogInfo(
                            "[SharedPov] Smartphone no longer active → auto-exit HOLDER mode.");
                        Exit();
                        return;
                    }
                }
                else
                {
                    _smartphoneLostTimer = 0f;
                }
            }

            // VIEWER : exit manuel + forward E + drive caméra spectateur
            if (IsViewer)
            {
                try
                {
                    if (Input.GetKeyDown(ManualExitKey))
                    {
                        MiSideCoopPlugin.Logger?.LogInfo(
                            "[SharedPov] F7 pressed → manual exit VIEWER mode.");
                        Exit();
                        return;
                    }

                    if (Input.GetKeyDown(KeyCode.E))
                    {
                        ForwardKey("E");
                    }
                }
                catch { /* Input peut throw en menu */ }

                // Drive spectator camera vers la cible
                if (_spectatorCam != null && _hasTarget)
                {
                    var tr = _spectatorCam.transform;
                    // Snap immédiat la 1ʳᵉ fois pour éviter un balayage depuis (0,0,0)
                    if (tr.position == Vector3.zero)
                    {
                        tr.position = _targetHeadPos;
                        tr.rotation = _targetHeadRot;
                    }
                    else
                    {
                        float t = 1f - Mathf.Exp(-CamLerpSpeed * Time.deltaTime);
                        tr.position = Vector3.Lerp(tr.position, _targetHeadPos, t);
                        tr.rotation = Quaternion.Slerp(tr.rotation, _targetHeadRot, t);
                    }
                }
            }
        }

        // ── Setup / teardown spectator camera ─────────────────────────────────
        private void SetupSpectatorCamera()
        {
            // 1) Désactive la MainCamera locale
            _myMainCam = Camera.main;
            if (_myMainCam != null)
            {
                _myMainCamWasEnabled = _myMainCam.enabled;
                _myMainCam.enabled = false;
                // Désactive aussi son AudioListener pour éviter doublon
                try
                {
                    var al = _myMainCam.GetComponent<AudioListener>();
                    if (al != null) al.enabled = false;
                }
                catch { }
                MiSideCoopPlugin.Logger?.LogDebug(
                    $"[SharedPov] Local MainCamera '{_myMainCam.gameObject.name}' disabled.");
            }
            else
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    "[SharedPov] No Camera.main found — spectator will render but local cam not disabled.");
            }

            // 2) Crée la spectator camera
            _spectatorCamGo = new GameObject("CoopSpectatorCamera");
            // DontDestroyOnLoad pour survivre aux transitions de scène intra-pickup
            try { DontDestroyOnLoad(_spectatorCamGo); } catch { }
            _spectatorCam = _spectatorCamGo.AddComponent<Camera>();
            _spectatorCam.fieldOfView = _myMainCam != null ? _myMainCam.fieldOfView : 60f;
            _spectatorCam.nearClipPlane = _myMainCam != null ? _myMainCam.nearClipPlane : 0.1f;
            _spectatorCam.farClipPlane  = _myMainCam != null ? _myMainCam.farClipPlane  : 1000f;
            _spectatorCam.clearFlags = _myMainCam != null ? _myMainCam.clearFlags : CameraClearFlags.Skybox;
            _spectatorCam.cullingMask = _myMainCam != null ? _myMainCam.cullingMask : -1;
            _spectatorCam.depth = 100f; // s'assurer qu'elle render au-dessus
            _spectatorCam.tag = "MainCamera"; // certains scripts MiSide font Camera.main → on devient main

            // AudioListener sur la spectator pour entendre l'audio 3D
            _spectatorAudio = _spectatorCamGo.AddComponent<AudioListener>();

            // Position initiale (sera écrasée par OnRemoteState dès le 1er tick)
            _spectatorCamGo.transform.position = Vector3.zero;
            _spectatorCamGo.transform.rotation = Quaternion.identity;

            MiSideCoopPlugin.Logger?.LogInfo(
                "[SharedPov] CoopSpectatorCamera created (depth=100, tagged MainCamera).");
        }

        private void TeardownSpectatorCamera()
        {
            // 1) Détruit la spectator camera
            if (_spectatorCamGo != null)
            {
                try { Destroy(_spectatorCamGo); } catch { }
                _spectatorCamGo = null;
                _spectatorCam = null;
                _spectatorAudio = null;
                MiSideCoopPlugin.Logger?.LogDebug("[SharedPov] CoopSpectatorCamera destroyed.");
            }

            // 2) Restaure la MainCamera locale
            if (_myMainCam != null)
            {
                try
                {
                    _myMainCam.enabled = _myMainCamWasEnabled;
                    var al = _myMainCam.GetComponent<AudioListener>();
                    if (al != null) al.enabled = true;
                }
                catch { }
                MiSideCoopPlugin.Logger?.LogDebug(
                    $"[SharedPov] Local MainCamera '{_myMainCam.gameObject.name}' restored (enabled={_myMainCamWasEnabled}).");
            }
            _myMainCam = null;
        }

        // ── Hide / restore le clone 3D du peer (le ghost qu'on voit) ─────────
        private void HideRemoteGhost()
        {
            var nm = CoopNetworkManager.Instance;
            if (nm == null) return;

            // Le ghost qu'on veut cacher = l'avatar distant. Côté guest, c'est
            // _player1 (host ghost). Côté host, c'est _player2 (guest ghost).
            MonoBehaviour ghostAvatar = nm.IsHost
                ? (MonoBehaviour)GetField<Player2Avatar>(nm, "_player2")
                : (MonoBehaviour)GetField<Player1Avatar>(nm, "_player1");
            if (ghostAvatar == null) return;

            try
            {
                _hiddenGhost = ghostAvatar.gameObject;
                _hiddenGhostWasActive = _hiddenGhost.activeSelf;
                if (_hiddenGhostWasActive) _hiddenGhost.SetActive(false);
                MiSideCoopPlugin.Logger?.LogDebug(
                    $"[SharedPov] Remote ghost '{_hiddenGhost.name}' hidden during shared POV.");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning($"[SharedPov] HideRemoteGhost: {ex.Message}");
            }
        }

        private void RestoreRemoteGhost()
        {
            if (_hiddenGhost == null) return;
            try
            {
                if (_hiddenGhostWasActive) _hiddenGhost.SetActive(true);
            }
            catch { }
            _hiddenGhost = null;
        }

        // ── Forward de touche au holder ───────────────────────────────────────
        private void ForwardKey(string keyName)
        {
            // Throttle pour éviter le spam si l'utilisateur maintient E
            if (Time.unscaledTime - _lastForwardTime < ForwardCooldown) return;
            _lastForwardTime = Time.unscaledTime;

            try
            {
                Vector3 origin = _spectatorCam != null ? _spectatorCam.transform.position : Vector3.zero;
                CoopNetworkManager.Instance?.BroadcastInputForward(keyName, origin);
                MiSideCoopPlugin.Logger?.LogDebug($"[SharedPov] Forwarded '{keyName}' to holder.");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning($"[SharedPov] ForwardKey '{keyName}': {ex.Message}");
            }
        }

        // ── Côté holder : déclenche un interact local quand le peer envoie E ─
        private void TryTriggerLocalInteract()
        {
            // Stratégie : raycast depuis la MainCamera locale vers l'avant (~2m)
            // → on cherche un ObjectInteractive sur l'hit ou ses parents → Click().
            try
            {
                var cam = Camera.main;
                if (cam == null)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        "[SharedPov] TryTriggerLocalInteract: no Camera.main on holder.");
                    return;
                }

                var ray = new Ray(cam.transform.position, cam.transform.forward);
                if (Physics.Raycast(ray, out var hit, 3f, ~0, QueryTriggerInteraction.Collide))
                {
                    var interactiveType = MiSideCoop.Patches.Il2CppTypeResolver.FindType("ObjectInteractive");
                    if (interactiveType == null)
                    {
                        MiSideCoopPlugin.Logger?.LogWarning(
                            "[SharedPov] ObjectInteractive type not found via resolver.");
                        return;
                    }

                    // Cherche un ObjectInteractive sur le hit ou ses parents
                    var t = hit.transform;
                    Component target = null;
                    while (t != null && target == null)
                    {
                        try { target = t.GetComponent(interactiveType); } catch { }
                        t = t.parent;
                    }

                    if (target == null)
                    {
                        MiSideCoopPlugin.Logger?.LogDebug(
                            $"[SharedPov] Hit '{hit.collider?.name}' but no ObjectInteractive in parents → ignored.");
                        return;
                    }

                    // Invoke Click() via reflection
                    var clickMethod = interactiveType.GetMethod("Click");
                    if (clickMethod != null)
                    {
                        clickMethod.Invoke(target, null);
                        MiSideCoopPlugin.Logger?.LogInfo(
                            $"[SharedPov] Remote E → invoked Click() on '{target.gameObject.name}'.");
                    }
                    else
                    {
                        MiSideCoopPlugin.Logger?.LogWarning(
                            "[SharedPov] ObjectInteractive.Click method not found.");
                    }
                }
                else
                {
                    MiSideCoopPlugin.Logger?.LogDebug(
                        "[SharedPov] Remote E → raycast missed (no interactive in front).");
                }
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[SharedPov] TryTriggerLocalInteract failed: {ex.Message}");
            }
        }

        // ── v1.6.7 — Smartphone activation watcher (côté host) ───────────────
        //
        // Poll cheap : on garde une référence cachée du GameObject Smartphone
        // (re-cherchée toutes les 2s pour gérer les changements de scène), et
        // on regarde activeInHierarchy chaque frame. Quand on détecte le
        // passage inactive → active, le HOST devient automatiquement HOLDER
        // et broadcast au client pour qu'il passe VIEWER.
        //
        // Seul le host déclenche la transition pour éviter les double-claims :
        // la cutscene "take phone" s'exécute identiquement sur les deux PC, et
        // sans cette convention, les deux deviendraient holder simultanément.
        //
        private void TickSmartphoneWatcher()
        {
            // v1.6.8 — DÉSACTIVÉ. Le mécanisme "Shared POV" cassait la vue 1ère
            // personne du viewer (sa MainCamera était désactivée et remplacée
            // par une spectator cam → il ne voyait plus son propre téléphone /
            // couteau dans ses mains). L'utilisateur veut que LES DEUX joueurs
            // voient l'item dans leurs propres mains (et sur le clone du peer).
            //
            // La synchronisation des hand-items est désormais assurée par
            // <see cref="HandItemSync"/> qui poll l'état activeInHierarchy
            // des items 1ère-personne ET du 3rd-person Right item, et qui
            // applique l'activation chez le peer (1ère personne locale +
            // sur le clone qui représente le sender).
            return;
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static GameObject SafeFind(string path)
        {
            try { return GameObject.Find(path); }
            catch { return null; }
        }

        // Petit helper pour récupérer un champ privé via reflection (Player1/Player2).
        private static T GetField<T>(object src, string fieldName) where T : class
        {
            if (src == null) return null;
            try
            {
                var f = src.GetType().GetField(fieldName,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                return f?.GetValue(src) as T;
            }
            catch { return null; }
        }
    }
}

using System;
using System.IO;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;
using MiSideCoop.Avatars;

namespace MiSideCoop.Network
{
    /// <summary>
    /// Gestionnaire réseau principal du mod co-op.
    ///
    /// Architecture sans dépendance externe : le transport est un TCP brut
    /// (System.Net.Sockets). Les messages sont sérialisés en binaire et
    /// dispatchés depuis le main thread Unity via Update().
    /// </summary>
    public class CoopNetworkManager : MonoBehaviour
    {

        // ── Singleton ─────────────────────────────────────────────────────────
        public static CoopNetworkManager Instance { get; private set; }

        // ── État de connexion ─────────────────────────────────────────────────
        public bool   IsConnected         => _isHost || _isClient;
        public bool   IsHost              => _isHost;
        public string RoomCode            { get; private set; }
        public string ConnectedPlayerName { get; private set; } = "...";

        private bool _isHost;
        private bool _isClient;

        // ── Avatars ───────────────────────────────────────────────────────────
        private Player1Avatar _player1;
        private Player2Avatar _player2;

        // v1.4.5 — Tracking "spawné via fallback synthétique" pour pouvoir
        // upgrader vers un clone 3D RealMcCloner dès que le MC local devient
        // disponible (ex: après que l'host quitte le menu principal et entre
        // en jeu). Sans ça, la capsule bleue restait là à vie même après
        // que MC ait été chargé en scène.
        private bool _player1IsSynthetic;
        private bool _player2IsSynthetic;

        // ── Transport ─────────────────────────────────────────────────────────
        private CoopTcpTransport _tx;

        // ── Sync cadence ──────────────────────────────────────────────────────
        private const float SyncInterval = 0.05f;  // 20 Hz
        private float _syncTimer;

        // ── Lifecycle ─────────────────────────────────────────────────────────
        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            _tx = new CoopTcpTransport();
            _tx.OnRemoteConnected    = OnRemotePeerConnected;
            _tx.OnRemoteDisconnected = OnRemotePeerDisconnected;
            _tx.OnConnectedToHost    = OnConnectedToHost;
            _tx.OnConnectionError    = OnRelayConnectionError;
        }

        private void OnDestroy()
        {
            StopCoop();
            if (Instance == this) Instance = null;
        }

        // ── API publique ──────────────────────────────────────────────────────

        public void StartHost(string roomCode)
        {
            RoomCode = roomCode;
            _isHost  = true;
            var host = MiSideCoopPlugin.RelayHost.Value;
            var port = MiSideCoopPlugin.RelayPort.Value;
            _tx.StartHost(host, port, roomCode);
            MiSideCoopPlugin.Logger.LogInfo(
                $"[Co-op] Connecting to relay {host}:{port} as HOST. Code: {roomCode}");
            SpawnPlayer1Local();
        }

        public void StartClient(string roomCode)
        {
            _isClient = true;
            RoomCode  = roomCode;
            var host = MiSideCoopPlugin.RelayHost.Value;
            var port = MiSideCoopPlugin.RelayPort.Value;
            _tx.StartGuest(host, port, roomCode);
            MiSideCoopPlugin.Logger.LogInfo(
                $"[Co-op] Connecting to relay {host}:{port} as GUEST. Code: {roomCode}");
        }

        public void StopCoop()
        {
            _tx?.Stop();
            _isHost = false;
            _isClient = false;
            DestroyRemoteAvatars();
            RoomCode = null;
            ConnectedPlayerName = "...";
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Co-op session ended.");
        }

        // ── Broadcasts publics (appelés depuis les Patches) ───────────────────
        public void BroadcastAction(PlayerActionMessage msg)     => _tx.Send(msg);
        public void BroadcastObjectSync(ObjectSyncMessage msg)   => _tx.Send(msg);
        public void BroadcastSceneChange(SceneChangeMessage msg) { if (_isHost) _tx.Send(msg); }
        public void BroadcastCutsceneMessage(CutsceneMessage msg){ if (_isHost) _tx.Send(msg); }

        // ── Update : envoi de l'état local + pump des messages entrants ───────
        private float _retrySpawnTimer;
        private void Update()
        {
            // 0bis) Pump les notifications du transport (callbacks marshalées
            //       depuis les threads socket → main thread Unity). À appeler
            //       MÊME si !IsConnected pour récupérer les erreurs de connexion
            //       et l'événement "PeerJoined" qui peut arriver pendant la
            //       phase de pairing initiale.
            _tx?.Pump();

            if (!IsConnected) return;

            // 0) Retry-spawn différé : si l'avatar local ou distant n'a pas pu
            //    être créé (pas de PlayerMove côté host, ou GameObject détruit
            //    sur changement de scène), on re-tente toutes les 2s. Dès que
            //    le joueur entre en jeu, le MC apparaît → spawn OK.
            //
            // v1.3.8 — on retry aussi pour Player2 côté guest (les avatars
            // synthétiques ne sont pas DontDestroyOnLoad → détruits au scene
            // change, doivent être re-spawnés).
            _retrySpawnTimer += Time.deltaTime;
            if (_retrySpawnTimer >= 2f)
            {
                _retrySpawnTimer = 0f;
                if (_isHost)
                {
                    if (_player1 == null) SpawnPlayer1Local();
                    // Player2 (guest ghost) est respawné via OnRemotePeerConnected,
                    // ou via retry ici si l'avatar a été détruit par un scene change.
                    if (_player2 == null && !string.IsNullOrEmpty(ConnectedPlayerName)
                        && ConnectedPlayerName != "...")
                        SpawnPlayer2Remote();
                    // v1.4.5 — Upgrade synthetic → 3D clone si le MC local
                    // est désormais disponible (cas typique : host a créé
                    // la room depuis le menu, et vient d'entrer en jeu).
                    if (_player2IsSynthetic && _player2 != null)
                        TryUpgradePlayer2ToClone();
                }
                else if (_isClient)
                {
                    if (_player1 == null) SpawnPlayer1Remote();
                    if (_player2 == null) SpawnPlayer2Local();
                    // v1.4.5 — Idem côté guest : upgrade Player1 fantôme.
                    if (_player1IsSynthetic && _player1 != null)
                        TryUpgradePlayer1ToClone();
                }
            }

            // 1) Dispatch des messages reçus (thread Unity)
            while (_tx.Incoming.TryDequeue(out var item))
                Dispatch(item.id, item.payload);

            // 2) Envoi de l'état local à cadence fixe
            _syncTimer += Time.deltaTime;
            if (_syncTimer < SyncInterval) return;
            _syncTimer = 0f;
            SendLocalState();
        }

        [HideFromIl2Cpp]
        private void Dispatch(MsgId id, byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var br = new BinaryReader(ms);
            switch (id)
            {
                case MsgId.PlayerState:
                    { var m = new PlayerStateMessage();  m.Read(br); OnPlayerState(m); break; }
                case MsgId.PlayerAction:
                    { var m = new PlayerActionMessage(); m.Read(br); OnPlayerAction(m); break; }
                case MsgId.SceneChange:
                    { var m = new SceneChangeMessage();  m.Read(br); OnSceneChange(m); break; }
                case MsgId.Cutscene:
                    { var m = new CutsceneMessage();     m.Read(br); OnCutscene(m); break; }
                case MsgId.RoomJoin:
                    { var m = new RoomJoinMessage();     m.Read(br); OnRoomJoin(m); break; }
                case MsgId.ObjectSync:
                    { var m = new ObjectSyncMessage();   m.Read(br); OnObjectSync(m); break; }
                case MsgId.GameLaunch:
                    { var m = new GameLaunchMessage();   m.Read(br); OnGameLaunch(m); break; }
            }
        }

        // ── Envoi état local ──────────────────────────────────────────────────
        private void SendLocalState()
        {
            MonoBehaviour localAvatar = _isHost ? (MonoBehaviour)_player1 : _player2;
            if (localAvatar == null) return;

            var animator = localAvatar.GetComponent<Animator>()
                        ?? localAvatar.GetComponentInChildrenSafe<Animator>();

            var msg = new PlayerStateMessage
            {
                Position       = localAvatar.transform.position,
                Rotation       = localAvatar.transform.rotation,
                MoveSpeed      = SampleAnimatorSpeed(animator),
                AnimationState = SampleAnimatorState(animator),
                LookDirection  = localAvatar.transform.forward,
                PlayerName     = MiSideCoopPlugin.LocalPlayerName.Value
            };
            _tx.Send(msg);
        }

        // ── Handlers de connexion ────────────────────────────────────────────
        private void OnRemotePeerConnected()
        {
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Remote peer connected.");
            if (_isHost) SpawnPlayer2Remote();
        }

        private void OnRemotePeerDisconnected()
        {
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Remote peer disconnected.");
            DestroyRemoteAvatars();
            // v1.3.9 — Reset ConnectedPlayerName pour que le retry-spawn
            // côté host arrête de recréer Player2 en boucle après une
            // déconnexion. Sans ça, l'host gardait "lowserz" dans
            // ConnectedPlayerName, le retry-spawn voyait _player2 == null
            // (parce qu'on vient de Destroy l'avatar distant) et le
            // ressuscitait toutes les 2 secondes indéfiniment.
            ConnectedPlayerName = "...";
        }

        private void OnConnectedToHost()
        {
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Connected to host.");
            _tx.Send(new RoomJoinMessage
            {
                PlayerName = MiSideCoopPlugin.LocalPlayerName.Value,
                PlayerRole = 2
            });
            SpawnPlayer2Local();
            SpawnPlayer1Remote();
        }

        private void OnRelayConnectionError(string error)
        {
            MiSideCoopPlugin.Logger.LogError($"[Co-op] Relay error: {error}");
            _isHost = false;
            _isClient = false;
            RoomCode = null;
        }

        // ── Handlers de messages applicatifs ─────────────────────────────────
        private void OnPlayerState(PlayerStateMessage msg)
        {
            // L'avatar distant correspond au rôle opposé du local
            if (_isHost) _player2?.ApplyRemoteState(msg);
            else         _player1?.ApplyRemoteState(msg);
        }

        private void OnPlayerAction(PlayerActionMessage msg)
        {
            var remoteAvatar = _isHost ? (MonoBehaviour)_player2 : _player1;
            remoteAvatar?.GetComponent<AvatarAnimatorSync>()?.PlayActionAnimation(msg.ActionType);

            if (msg.ActionType == "PickUp" && !string.IsNullOrEmpty(msg.TargetObjectId))
            {
                var obj = GameObject.Find(msg.TargetObjectId);
                if (obj != null) obj.SetActive(false);
            }
        }

        private void OnRoomJoin(RoomJoinMessage msg)
        {
            ConnectedPlayerName = msg.PlayerName;
            if (_player2 != null) _player2.Initialize(msg.PlayerName, false);
            MiSideCoopPlugin.Logger.LogInfo($"[Co-op] Peer identified: {msg.PlayerName}");
        }

        private void OnSceneChange(SceneChangeMessage msg)
        {
            if (_isHost) return; // l'hôte initie, le client suit
            // v1.4.2 — PIVOT (suite du v1.4.1) :
            //
            // Avant v1.4.2, on appelait SceneManager.LoadScene(msg.SceneName)
            // côté guest pour forcer la scène à matcher celle du host. Mais
            // ce LoadScene bypass complètement la machine d'état MiSide
            // (intro, init save state, audio, etc.). MiSide démarre Scene 1
            // sans les données dont ses scripts ont besoin → NullReferenceException
            // pendant le load → kill de la pump TCP → guest disconnect.
            //
            // Dans l'architecture co-présence visuelle (v1.4.1), forcer le
            // scene sync n'a plus de sens : chaque joueur joue MiSide
            // normalement sur sa machine. Quand les deux sont naturellement
            // dans la même scène, les fantômes s'affichent. Sinon, ils ne
            // se voient pas (cohérent).
            //
            // → On garde le LOG informationnel (utile pour debug et pour que
            //   l'utilisateur sache où en est le peer), mais on n'appelle
            //   plus SceneManager.LoadScene.
            MiSideCoopPlugin.Logger.LogInfo(
                $"[Co-op] Peer is now in scene '{msg.SceneName}' (we stay in our own scene).");
        }

        private void OnCutscene(CutsceneMessage msg)
            => GameStateSync.Instance?.TriggerCutscene(msg.CutsceneId, msg.Start);

        private void OnObjectSync(ObjectSyncMessage msg)
        {
            var obj = GameObject.Find(msg.ObjectId);
            if (obj == null) return;
            obj.SetActive(msg.IsActive);
            if (msg.IsActive) obj.transform.position = msg.Position;
        }

        // ── v1.5.0 — Lancement synchronisé "Nouvelle Partie" ─────────────────
        private void OnGameLaunch(GameLaunchMessage msg)
        {
            if (_isHost) return; // l'hôte initie, on n'echo pas chez soi
            MiSideCoopPlugin.Logger.LogInfo(
                "[Co-op] Host pressed DÉMARRER — invoking 'Nouvelle Partie' on guest.");
            bool ok = MiSideCoop.UI.MenuButtonClicker.ClickNewGame();
            if (!ok)
            {
                MiSideCoopPlugin.Logger.LogWarning(
                    "[Co-op] Could not auto-click 'Nouvelle Partie' on guest "
                  + "(button not found — probably already in-game or different menu). "
                  + "The guest can still click Nouvelle Partie manually.");
            }
        }

        /// <summary>
        /// Envoyé par l'hôte uniquement (appelé par le bouton "DÉMARRER" du
        /// modal co-op). Le guest reçoit GameLaunchMessage et déclenche
        /// localement le bouton "Nouvelle Partie" du menu MiSide.
        /// </summary>
        public void BroadcastGameLaunch()
        {
            if (!_isHost || !IsConnected)
            {
                MiSideCoopPlugin.Logger.LogWarning(
                    "[Co-op] BroadcastGameLaunch ignored — not host or not connected.");
                return;
            }
            _tx.Send(new GameLaunchMessage());
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] GameLaunch broadcasted to guest.");
        }

        // ── Gestion des avatars ───────────────────────────────────────────────
        //
        // Chaque Spawn est enveloppé pour qu'un échec IL2CPP n'interrompe pas
        // la séquence de connexion : on log l'erreur et on continue. Sans ça,
        // un crash dans GetComponent/AddComponent stoppait tout le clic UI.
        //
        private bool _player1DeferredLogged;
        private void SpawnPlayer1Local()
        {
            try
            {
                var mcGo = FindMCGameObject();
                if (mcGo == null)
                {
                    // ── v1.2.3 FIX MITA ──
                    // Quand on appuie "Create Room" depuis le menu principal,
                    // PlayerMove n'existe pas encore (pas en jeu). L'ancienne
                    // version créait un CreateDefaultHumanoid OU pire, matchait
                    // Mita via le nom "Person" → Player1Avatar collait sur elle
                    // et activait sa caméra enfant / interférait avec son anim.
                    //
                    // Nouvelle stratégie : pas d'avatar tant qu'on n'est pas en jeu.
                    // Le spawn sera ré-essayé via Update() à intervalle régulier
                    // jusqu'à ce qu'une scène avec PlayerMove se charge.
                    if (!_player1DeferredLogged)
                    {
                        _player1DeferredLogged = true;
                        MiSideCoopPlugin.Logger.LogInfo(
                            "[Co-op] Host avatar spawn deferred — no MC GameObject yet " +
                            "(probably still in main menu). Will retry silently on each scene.");
                    }
                    return;
                }
                _player1DeferredLogged = false; // reset si on a retrouvé le MC
                // v1.3.6 — Le AddComponent(typeof(T)) introduit en v1.3.5
                // (System.Type) est STRIPPÉ en IL2CPP MiSide. On utilise
                // notre helper Il2CppAddComponentHelper.AddComponentSafe<T>
                // qui tente d'abord AddComponent(Il2CppSystem.Type) via
                // réflexion (path IL2CPP-safe définitive), puis fallback
                // sur AddComponent<T>() générique pour les types enregistrés.
                _player1 = mcGo.GetComponent<Player1Avatar>()
                        ?? mcGo.AddComponentSafe<Player1Avatar>();
                if (_player1 == null)
                {
                    MiSideCoopPlugin.Logger.LogError(
                        "[Co-op] SpawnPlayer1Local: Player1Avatar component creation returned null.");
                    return;
                }
                _player1.Initialize(MiSideCoopPlugin.LocalPlayerName.Value, true);
                MiSideCoopPlugin.Logger.LogInfo(
                    $"[Co-op] Host avatar (Player1) initialized on '{mcGo.name}'.");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger.LogError($"[Co-op] SpawnPlayer1Local failed: {ex.Message}");
            }
        }

        private void SpawnPlayer2Remote()
        {
            try
            {
                // v1.4.9 — On ne spawn QUE si le MC local est dispo (clone 3D possible).
                // Si pas dispo (host encore au menu principal), on diffère le spawn ;
                // le retry-spawn (toutes les 2s) re-tentera quand le MC apparaîtra.
                // Avantage : aucune capsule bleue ne s'affiche dans le menu.
                var cloned = MiSideCoop.Avatars.RealMcCloner.CloneLocalMc("Player2_Guest");
                if (cloned == null)
                {
                    MiSideCoopPlugin.Logger.LogInfo(
                        "[Co-op] SpawnPlayer2Remote deferred — MC not yet in scene "
                      + "(host probably still in main menu). Retry-spawn will recheck every 2s.");
                    _player2IsSynthetic = true; // marqueur pour que retry-spawn re-tente
                    return;
                }
                _player2IsSynthetic = false;
                _player2 = cloned.AddComponentSafe<Player2Avatar>();
                if (_player2 == null)
                {
                    MiSideCoopPlugin.Logger.LogError(
                        "[Co-op] SpawnPlayer2Remote: Player2Avatar component creation returned null.");
                    return;
                }
                _player2.Initialize(ConnectedPlayerName, false);
                // Clone 3D = textures MiSide originales préservées (pas de ApplySkinColor).
                MiSideCoopPlugin.Logger.LogInfo(
                    "[Co-op] Guest avatar (Player2) spawned on host (3D clone).");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger.LogError($"[Co-op] SpawnPlayer2Remote failed: {ex.Message}");
            }
        }

        private bool _player2LocalDeferredLogged;
        private void SpawnPlayer2Local()
        {
            // v1.4.1 — PIVOT ARCHITECTURAL :
            //
            // Avant : on créait un GameObject synthétique 'Player2_Self'
            // (capsule + sphère) qu'on prétendait être le "corps du guest"
            // avec sa propre caméra MainCamera. Conflit avec la MainCamera du
            // VRAI MC MiSide qui tournait en parallèle sur la machine du
            // guest → NRE et écran gris-bleu uni.
            //
            // Maintenant : sur la machine du guest, MiSide tourne normalement
            // avec son MC 'Player' local. On attache Player2Avatar
            // (isLocal=true) DIRECTEMENT sur ce MC réel. PlayerAvatar.transform
            // pointe alors sur le transform du MC, donc SendLocalState envoie
            // automatiquement les coordonnées réelles du joueur au host.
            //
            // Côté host, l'avatar visuel du guest reste un humanoïde synthétique
            // (Player2_Guest, créé par SpawnPlayer2Remote) qui interpolera vers
            // les positions reçues. Symétrique à ce que SpawnPlayer1Local /
            // SpawnPlayer1Remote font pour le host.
            try
            {
                if (_player2 != null) return; // déjà attaché

                var mcGo = FindMCGameObject();
                if (mcGo == null)
                {
                    if (!_player2LocalDeferredLogged)
                    {
                        _player2LocalDeferredLogged = true;
                        MiSideCoopPlugin.Logger.LogInfo(
                            "[Co-op] Guest local avatar spawn deferred — no MC GameObject yet "
                          + "(probably still in main menu). Will retry silently on each scene.");
                    }
                    return;
                }
                _player2LocalDeferredLogged = false;

                _player2 = mcGo.GetComponent<Player2Avatar>()
                        ?? mcGo.AddComponentSafe<Player2Avatar>();
                if (_player2 == null)
                {
                    MiSideCoopPlugin.Logger.LogError(
                        "[Co-op] SpawnPlayer2Local: Player2Avatar component creation returned null.");
                    return;
                }
                _player2.Initialize(MiSideCoopPlugin.LocalPlayerName.Value, true);
                MiSideCoopPlugin.Logger.LogInfo(
                    $"[Co-op] Guest local avatar (Player2) attached on real MC '{mcGo.name}'.");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger.LogError($"[Co-op] SpawnPlayer2Local failed: {ex.Message}");
            }
        }

        private void SpawnPlayer1Remote()
        {
            // v1.3.8 — REFONTE :
            //
            // En v1.3.7 cette méthode appelait FindMCGameObject() côté guest,
            // ce qui retournait `Player2_Self` (l'avatar local du guest) parce
            // que Camera.main est tagguée MainCamera sur Player2Camera → root =
            // Player2_Self. Conséquence : Player1Avatar (la représentation
            // distante du host) était attaché sur le PROPRE corps du guest.
            //
            // Le concept même était faux : il n'y a PAS de "MC du host" sur la
            // machine du guest. Le host est sur sa machine. Le guest doit juste
            // afficher un avatar fantôme synthétique (capsule humanoïde) qui
            // suit les positions reçues du réseau — exactement comme on fait
            // pour Player2 côté host (SpawnPlayer2Remote).
            //
            // → On crée un CreateDefaultHumanoid dédié, plus aucun FindMC.
            try
            {
                if (_player1 != null) return; // déjà spawné

                // v1.4.9 — Spawn différé tant que le MC local n'est pas dispo
                // (pas de capsule bleue dans le menu).
                var cloned = MiSideCoop.Avatars.RealMcCloner.CloneLocalMc("Player1_Host_Remote");
                if (cloned == null)
                {
                    MiSideCoopPlugin.Logger.LogInfo(
                        "[Co-op] SpawnPlayer1Remote deferred — MC not yet in scene. "
                      + "Retry-spawn will recheck every 2s.");
                    _player1IsSynthetic = true;
                    return;
                }
                _player1IsSynthetic = false;
                _player1 = cloned.AddComponentSafe<Player1Avatar>();
                if (_player1 == null)
                {
                    MiSideCoopPlugin.Logger.LogError(
                        "[Co-op] SpawnPlayer1Remote: Player1Avatar component creation returned null.");
                    return;
                }
                _player1.Initialize("Host", false);
                MiSideCoopPlugin.Logger.LogInfo(
                    "[Co-op] Remote Player1 (host ghost) spawned as 3D MC clone.");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger.LogError($"[Co-op] SpawnPlayer1Remote failed: {ex.Message}");
            }
        }

        // v1.4.5 — UPGRADE SYNTHETIC → 3D CLONE
        //
        // Quand un avatar distant a été créé en mode fallback (capsule + sphère)
        // parce que le MC local n'était pas encore en scène (typiquement quand
        // l'host crée la room depuis le menu principal), on re-tente le clone
        // 3D toutes les 2s via la boucle retry-spawn. Dès que le MC apparaît,
        // on Destroy la capsule et on la remplace par un vrai clone 3D.
        //
        // Note : pour les avatars LOCAUX (Player1 côté host, Player2 côté guest),
        // pas besoin d'upgrade — ils sont attachés directement sur le MC réel.
        private void TryUpgradePlayer2ToClone()
        {
            // Pre-flight : MC local disponible ?
            var mc = MiSideCoop.Avatars.SceneDiagnostics.FindMcHeuristic();
            if (mc == null) return; // pas encore, on réessaiera au prochain tick

            try
            {
                var newClone = MiSideCoop.Avatars.RealMcCloner.CloneLocalMc("Player2_Guest");
                if (newClone == null) return; // Instantiate raté, reste synthétique

                // Préserve la dernière position interpolée pour éviter un teleport.
                Vector3 lastPos = _player2.transform.position;
                Quaternion lastRot = _player2.transform.rotation;

                Destroy(_player2.gameObject);
                _player2 = null;

                newClone.transform.position = lastPos;
                newClone.transform.rotation = lastRot;
                _player2 = newClone.AddComponentSafe<Player2Avatar>();
                if (_player2 == null)
                {
                    MiSideCoopPlugin.Logger.LogError(
                        "[Co-op] TryUpgradePlayer2ToClone: AddComponent failed on cloned MC.");
                    _player2IsSynthetic = true;
                    return;
                }
                _player2.Initialize(ConnectedPlayerName, false);
                // v1.4.8 — Clone 3D = pas de recolorage (textures originales).
                _player2IsSynthetic = false;
                MiSideCoopPlugin.Logger.LogInfo(
                    "[Co-op] Player2 upgraded synthetic → 3D MC clone (peer now visible as real character).");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger.LogError(
                    $"[Co-op] TryUpgradePlayer2ToClone failed: {ex.Message}");
            }
        }

        private void TryUpgradePlayer1ToClone()
        {
            var mc = MiSideCoop.Avatars.SceneDiagnostics.FindMcHeuristic();
            if (mc == null) return;

            try
            {
                var newClone = MiSideCoop.Avatars.RealMcCloner.CloneLocalMc("Player1_Host_Remote");
                if (newClone == null) return;

                Vector3 lastPos = _player1.transform.position;
                Quaternion lastRot = _player1.transform.rotation;
                Destroy(_player1.gameObject);
                _player1 = null;

                newClone.transform.position = lastPos;
                newClone.transform.rotation = lastRot;
                _player1 = newClone.AddComponentSafe<Player1Avatar>();
                if (_player1 == null)
                {
                    MiSideCoopPlugin.Logger.LogError(
                        "[Co-op] TryUpgradePlayer1ToClone: AddComponent failed on cloned MC.");
                    _player1IsSynthetic = true;
                    return;
                }
                _player1.Initialize("Host", false);
                _player1IsSynthetic = false;
                MiSideCoopPlugin.Logger.LogInfo(
                    "[Co-op] Player1 upgraded synthetic → 3D MC clone (peer now visible as real character).");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger.LogError(
                    $"[Co-op] TryUpgradePlayer1ToClone failed: {ex.Message}");
            }
        }


        private void DestroyRemoteAvatars()
        {
            if (_player2 != null && !_player2.IsLocalPlayer)
            {
                Destroy(_player2.gameObject); _player2 = null;
            }
            if (_player1 != null && !_player1.IsLocalPlayer)
            {
                Destroy(_player1.gameObject); _player1 = null;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        //
        // IL2CPP MiSide : Object.FindObjectOfType(Type), FindObjectsOfType(...) et
        // toutes les API génériques de recherche sont strippées. On utilise
        // uniquement GameObject.Find(string) qui est prouvé fonctionnel par les
        // logs (GameStateSync.OnSceneChanged appelle GameObject.Find("SpawnPoint")
        // avec succès).
        //
        // Chaque appel est isolé dans sa propre sous-méthode pour éviter qu'une
        // éventuelle API strippée fasse échouer la JIT-compilation de toute la
        // méthode parente.
        //
        private static GameObject FindMCGameObject()
        {
            // v1.3.7 — Délégation à SceneDiagnostics.FindMcHeuristic qui :
            //   1) Essaie une liste élargie de noms candidats (incluant les
            //      variantes 0.93L : Mita_MC, Player_MC, MC_Player, Character...).
            //   2) Heuristique sous Camera.main.transform.root : descendant
            //      avec Rigidbody/CharacterController + Animator, nom non-Mita.
            //   3) Fallback : Camera.main.transform.root (comportement v1.3.6).
            //
            // L'utilisateur peut appuyer sur F9 en jeu pour dumper l'arbre complet
            // de la scène (cf. CoopBootstrap) → on identifie le vrai nom MC en
            // 0.93L et on l'ajoute à la liste de candidats sans Cpp2IL.
            return MiSideCoop.Avatars.SceneDiagnostics.FindMcHeuristic();
        }

        /// <summary>
        /// Filtre les noms de GameObjects typiques des écrans menu/UI/cinématiques
        /// pour éviter de prendre Mita ou un Canvas pour le MC.
        /// </summary>
        private static bool IsMenuRootName(string n)
        {
            if (string.IsNullOrEmpty(n)) return true;
            var lo = n.ToLowerInvariant();
            return lo.Contains("menu")  || lo.Contains("ui")      ||
                   lo.Contains("canvas")|| lo.Contains("mita")    ||
                   lo.Contains("splash")|| lo.Contains("title")   ||
                   lo.Contains("intro") || lo.Contains("cutscene")||
                   lo.Contains("person"); // safety net : "Person" (Mita au menu)
        }

        private static GameObject SafeGameObjectFind(string name)
        {
            try { return GameObject.Find(name); }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning($"[Co-op] GameObject.Find('{name}'): {ex.Message}");
                return null;
            }
        }

        private static GameObject SafeFindWithTag(string tag)
        {
            try { return GameObject.FindWithTag(tag); }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning($"[Co-op] FindWithTag('{tag}'): {ex.Message}");
                return null;
            }
        }

        private static GameObject CreateDefaultHumanoid(string goName)
        {
            var root = new GameObject(goName);

            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.transform.SetParent(root.transform);
            body.transform.localPosition = new Vector3(0, 1f, 0);
            body.transform.localScale    = new Vector3(0.4f, 0.9f, 0.4f);
            UnityEngine.Object.Destroy(body.GetComponent<CapsuleCollider>());

            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.transform.SetParent(root.transform);
            head.transform.localPosition = new Vector3(0, 2.1f, 0);
            head.transform.localScale    = Vector3.one * 0.35f;
            UnityEngine.Object.Destroy(head.GetComponent<SphereCollider>());

            var col = root.AddComponent<CapsuleCollider>();
            col.height    = 2f;
            col.center    = new Vector3(0, 1f, 0);
            col.isTrigger = true;

            // v1.3.9 — Marquer DontDestroyOnLoad pour que l'avatar survive
            // aux changements de scène. Sans ça, MiSide détruit notre
            // GameObject à chaque transition (SceneLoading → Scene 1 - ...),
            // ce qui :
            //   1) cassait les références cachées (NRE côté guest)
            //   2) forçait un respawn toutes les ~2s via retry-spawn
            //      (log spam "Guest avatar (Player2) spawned on host." en
            //       boucle, surtout après un disconnect non nettoyé)
            //   3) faisait disparaître visuellement l'avatar pendant les
            //      transitions de scène.
            // L'objet doit être à la racine (sans parent) pour que Unity
            // accepte DontDestroyOnLoad — c'est le cas par construction ici.
            try { UnityEngine.Object.DontDestroyOnLoad(root); }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] CreateDefaultHumanoid DontDestroyOnLoad: {ex.Message}");
            }

            return root;
        }

        private static string SampleAnimatorState(Animator anim)
        {
            if (anim == null) return "Idle";
            var info = anim.GetCurrentAnimatorStateInfo(0);
            if (info.IsName("Walk")    || info.IsName("Walking"))   return "Walk";
            if (info.IsName("Run")     || info.IsName("Running"))   return "Run";
            if (info.IsName("Crouch")  || info.IsName("Crouching")) return "Crouch";
            if (info.IsName("Interact"))                             return "Interact";
            if (info.IsName("PickUp")  || info.IsName("Pickup"))    return "PickUp";
            if (info.IsName("OpenDoor"))                             return "OpenDoor";
            if (info.IsName("Die")     || info.IsName("Death"))     return "Die";
            return "Idle";
        }

        private static float SampleAnimatorSpeed(Animator anim)
        {
            if (anim == null) return 0f;
            string[] candidates = { "Speed", "MoveSpeed", "Velocity", "BlendSpeed" };
            foreach (var c in candidates)
            {
                try { return anim.GetFloat(c); } catch { }
            }
            return 0f;
        }
    }
}

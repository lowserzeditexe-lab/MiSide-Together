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

            // 0) Retry-spawn différé : si l'avatar local n'a pas pu être créé
            //    (pas de PlayerMove au moment de StartHost/StartClient, ex:
            //    on était encore au menu principal), on re-tente toutes les 2s.
            //    Dès que le joueur entre en jeu, PlayerMove apparaît → spawn OK.
            _retrySpawnTimer += Time.deltaTime;
            if (_retrySpawnTimer >= 2f)
            {
                _retrySpawnTimer = 0f;
                if (_isHost && _player1 == null)
                    SpawnPlayer1Local();
                else if (_isClient && _player1 == null)
                    SpawnPlayer1Remote();
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
            MiSideCoopPlugin.Logger.LogInfo($"[Co-op] Scene change to '{msg.SceneName}'.");
            SceneManager.LoadScene(msg.SceneName);
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
                var go = CreateDefaultHumanoid("Player2_Guest");
                _player2 = go.AddComponentSafe<Player2Avatar>();
                if (_player2 == null)
                {
                    MiSideCoopPlugin.Logger.LogError(
                        "[Co-op] SpawnPlayer2Remote: Player2Avatar component creation returned null.");
                    return;
                }
                _player2.Initialize(ConnectedPlayerName, false);
                _player2.ApplySkinColor(MiSideCoopPlugin.Player2SkinColor.Value);
                MiSideCoopPlugin.Logger.LogInfo("[Co-op] Guest avatar (Player2) spawned on host.");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger.LogError($"[Co-op] SpawnPlayer2Remote failed: {ex.Message}");
            }
        }

        private void SpawnPlayer2Local()
        {
            try
            {
                var go = CreateDefaultHumanoid("Player2_Self");
                _player2 = go.AddComponentSafe<Player2Avatar>();
                if (_player2 == null)
                {
                    MiSideCoopPlugin.Logger.LogError(
                        "[Co-op] SpawnPlayer2Local: Player2Avatar component creation returned null.");
                    return;
                }
                _player2.Initialize(MiSideCoopPlugin.LocalPlayerName.Value, true);
                _player2.ApplySkinColor(MiSideCoopPlugin.Player2SkinColor.Value);
                MiSideCoopPlugin.Logger.LogInfo("[Co-op] Local avatar (Player2) initialized on client.");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger.LogError($"[Co-op] SpawnPlayer2Local failed: {ex.Message}");
            }
        }

        private bool _player1RemoteDeferredLogged;
        private void SpawnPlayer1Remote()
        {
            try
            {
                var go = FindMCGameObject();
                if (go == null)
                {
                    if (!_player1RemoteDeferredLogged)
                    {
                        _player1RemoteDeferredLogged = true;
                        MiSideCoopPlugin.Logger.LogInfo(
                            "[Co-op] Remote Player1 spawn deferred — no MC GameObject yet.");
                    }
                    return;
                }
                _player1RemoteDeferredLogged = false;
                _player1 = go.GetComponent<Player1Avatar>()
                        ?? go.AddComponentSafe<Player1Avatar>();
                if (_player1 == null)
                {
                    MiSideCoopPlugin.Logger.LogError(
                        "[Co-op] SpawnPlayer1Remote: Player1Avatar component creation returned null.");
                    return;
                }
                _player1.Initialize("Host", false);
                MiSideCoopPlugin.Logger.LogInfo(
                    $"[Co-op] Remote MC representation initialized on '{go.name}'.");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger.LogError($"[Co-op] SpawnPlayer1Remote failed: {ex.Message}");
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
            // ── v1.2.4 FIX MITA (révisé) ──
            // L'approche AccessTools.TypeByName("PlayerMove") + FindObjectOfType(Type)
            // de v1.2.3 a échoué : FindObjectOfType(Type) est STRIPPÉ en IL2CPP MiSide
            // (confirmé par log "Method not found: 'UnityEngine.Object FindObjectOfType
            // (System.Type)'"). On revient à des APIs IL2CPP-safe uniquement.
            //
            // Stratégie en 3 niveaux, du plus fiable au plus heuristique :

            // ── 1. Nom exact "male_mc" ──
            // Confirmé par dump IL2CPP de MiSide comme le nom officiel du MC.
            // 100 % spécifique → impossible de matcher Mita ou autre PNJ.
            var go = SafeGameObjectFind("male_mc");
            if (go != null) return go;

            // ── 2. Autres noms restreints ──
            // "Person" et "Player" volontairement RETIRÉS (matchaient Mita en v1.2.2−).
            // Tag fallback retiré aussi (trop générique).
            string[] names = { "MC", "PlayerCharacter", "MainCharacter", "PlayerController" };
            foreach (var n in names)
            {
                go = SafeGameObjectFind(n);
                if (go != null) return go;
            }

            // ── 3. Heuristique Camera.main.transform.root ──
            // En jeu MiSide, le MC porte sa caméra en enfant → root = MC.
            // Au menu principal, la caméra est sur "MenuCamera" / "Canvas" / etc.,
            // qu'on rejette via IsMenuRootName pour éviter de re-tomber sur Mita.
            try
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    var root = cam.transform.root.gameObject;
                    if (root != null && !IsMenuRootName(root.name))
                        return root;
                }
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning($"[Co-op] Camera.main.root lookup: {ex.Message}");
            }

            return null;
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

using System;
using Mirror;
using Telepathy;
using UnityEngine;
using UnityEngine.SceneManagement;
using MiSideCoop.Avatars;

namespace MiSideCoop.Network
{
    /// <summary>
    /// Gestionnaire réseau principal du mod co-op.
    /// Utilise l'API bas niveau de Mirror (messages manuels) pour une
    /// compatibilité maximale avec IL2CPP sans avoir besoin du Mirror Weaver.
    /// </summary>
    public class CoopNetworkManager : MonoBehaviour
    {
        public CoopNetworkManager(IntPtr ptr) : base(ptr) { }

        // ── Singleton ─────────────────────────────────────────────────────────
        public static CoopNetworkManager Instance { get; private set; }

        // ── État de connexion ─────────────────────────────────────────────────
        public bool   IsConnected          => _isHost || _isClient;
        public bool   IsHost               => _isHost;
        public string RoomCode             { get; private set; }
        public string ConnectedPlayerName  { get; private set; } = "...";

        private bool _isHost;
        private bool _isClient;

        // ── Avatars ───────────────────────────────────────────────────────────
        private Player1Avatar _player1;  // MC (hôte vu par l'invité, ou local si hôte)
        private Player2Avatar _player2;  // Second personnage (invité vu par l'hôte)

        // ── Transport ─────────────────────────────────────────────────────────
        private TelepathyTransport _transport;

        // ── Sync cadence ──────────────────────────────────────────────────────
        private const float SyncInterval = 0.05f;  // 20 Hz
        private float _syncTimer;

        // ── Lifecycle ─────────────────────────────────────────────────────────
        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            _transport = gameObject.AddComponent<TelepathyTransport>();
            _transport.port = (ushort)MiSideCoopPlugin.NetworkPort.Value;
            Transport.active = _transport;

            RegisterMessageHandlers();
        }

        private void OnDestroy()
        {
            StopCoop();
        }

        // ── Enregistrement des handlers Mirror ────────────────────────────────
        private void RegisterMessageHandlers()
        {
            // Côté serveur
            NetworkServer.RegisterHandler<PlayerStateMessage>  (Server_OnPlayerState,    false);
            NetworkServer.RegisterHandler<PlayerActionMessage> (Server_OnPlayerAction,   false);
            NetworkServer.RegisterHandler<RoomJoinMessage>     (Server_OnRoomJoin,       false);
            NetworkServer.RegisterHandler<ObjectSyncMessage>   (Server_OnObjectSync,     false);

            // Côté client
            NetworkClient.RegisterHandler<PlayerStateMessage>  (Client_OnPlayerState);
            NetworkClient.RegisterHandler<PlayerActionMessage> (Client_OnPlayerAction);
            NetworkClient.RegisterHandler<SceneChangeMessage>  (Client_OnSceneChange);
            NetworkClient.RegisterHandler<CutsceneMessage>     (Client_OnCutscene);
            NetworkClient.RegisterHandler<ObjectSyncMessage>   (Client_OnObjectSync);
        }

        // ── API publique ──────────────────────────────────────────────────────

        public void StartHost(string roomCode)
        {
            RoomCode = roomCode;
            _isHost  = true;

            NetworkServer.OnConnectedEvent    += Server_OnClientConnected;
            NetworkServer.OnDisconnectedEvent += Server_OnClientDisconnected;
            NetworkServer.Listen(2);

            MiSideCoopPlugin.Logger.LogInfo(
                $"[Co-op] Serveur démarré. Port {MiSideCoopPlugin.NetworkPort.Value}. Code : {roomCode}");

            SpawnPlayer1Local();
        }

        public void StartClient(string hostIp, int port)
        {
            _isClient = true;
            _transport.port = (ushort)port;

            NetworkClient.OnConnectedEvent    += Client_OnConnected;
            NetworkClient.OnDisconnectedEvent += Client_OnDisconnected;
            NetworkClient.Connect(hostIp);

            MiSideCoopPlugin.Logger.LogInfo($"[Co-op] Connexion vers {hostIp}:{port}…");
        }

        public void StopCoop()
        {
            if (_isHost)
            {
                NetworkServer.OnConnectedEvent    -= Server_OnClientConnected;
                NetworkServer.OnDisconnectedEvent -= Server_OnClientDisconnected;
                NetworkServer.Shutdown();
                _isHost = false;
            }
            if (_isClient)
            {
                NetworkClient.Disconnect();
                _isClient = false;
            }

            DestroyRemoteAvatars();
            RoomCode = null;
            ConnectedPlayerName = "...";
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Session co-op terminée.");
        }

        // ── Broadcasts publics (appelés depuis les Patches) ───────────────────

        public void BroadcastAction(PlayerActionMessage msg)
        {
            if (_isHost) NetworkServer.SendToAll(msg);
            else         NetworkClient.Send(msg);
        }

        public void BroadcastObjectSync(ObjectSyncMessage msg)
        {
            if (_isHost) NetworkServer.SendToAll(msg);
            else         NetworkClient.Send(msg);
        }

        public void BroadcastSceneChange(SceneChangeMessage msg)
        {
            if (_isHost) NetworkServer.SendToAll(msg);
        }

        public void BroadcastCutsceneMessage(CutsceneMessage msg)
        {
            if (_isHost) NetworkServer.SendToAll(msg);
        }

        // ── Update : envoi de l'état local ────────────────────────────────────
        private void Update()
        {
            if (!IsConnected) return;

            _syncTimer += Time.deltaTime;
            if (_syncTimer < SyncInterval) return;
            _syncTimer = 0f;

            SendLocalState();
        }

        private void SendLocalState()
        {
            // Détermine quel avatar est le nôtre
            MonoBehaviour localAvatar = _isHost ? (MonoBehaviour)_player1 : _player2;
            if (localAvatar == null) return;

            var animator = localAvatar.GetComponent<Animator>()
                        ?? localAvatar.GetComponentInChildren<Animator>();

            var msg = new PlayerStateMessage
            {
                Position       = localAvatar.transform.position,
                Rotation       = localAvatar.transform.rotation,
                MoveSpeed      = SampleAnimatorSpeed(animator),
                AnimationState = SampleAnimatorState(animator),
                LookDirection  = localAvatar.transform.forward,
                PlayerName     = MiSideCoopPlugin.LocalPlayerName.Value
            };

            if (_isHost) NetworkServer.SendToAll(msg);
            else         NetworkClient.Send(msg);
        }

        // ── Handlers serveur ─────────────────────────────────────────────────

        private void Server_OnClientConnected(NetworkConnectionToClient conn)
        {
            MiSideCoopPlugin.Logger.LogInfo($"[Co-op] Invité connecté (id={conn.connectionId}).");
            SpawnPlayer2Remote();
        }

        private void Server_OnClientDisconnected(NetworkConnectionToClient conn)
        {
            MiSideCoopPlugin.Logger.LogInfo($"[Co-op] Invité déconnecté (id={conn.connectionId}).");
            if (_player2 != null)
            {
                Destroy(_player2.gameObject);
                _player2 = null;
            }
        }

        private void Server_OnPlayerState(NetworkConnectionToClient conn, PlayerStateMessage msg)
        {
            // Redistribue l'état de l'invité à tous les clients (y compris l'hôte lui-même)
            NetworkServer.SendToAll(msg);
            _player2?.ApplyRemoteState(msg);
        }

        private void Server_OnPlayerAction(NetworkConnectionToClient conn, PlayerActionMessage msg)
        {
            NetworkServer.SendToAll(msg);
        }

        private void Server_OnRoomJoin(NetworkConnectionToClient conn, RoomJoinMessage msg)
        {
            ConnectedPlayerName = msg.PlayerName;
            if (_player2 != null) _player2.Initialize(msg.PlayerName, false);
            MiSideCoopPlugin.Logger.LogInfo($"[Co-op] Invité identifié : {msg.PlayerName}");
        }

        private void Server_OnObjectSync(NetworkConnectionToClient conn, ObjectSyncMessage msg)
        {
            NetworkServer.SendToAll(msg);
        }

        // ── Handlers client ───────────────────────────────────────────────────

        private void Client_OnConnected()
        {
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Connexion à l'hôte réussie.");

            NetworkClient.Send(new RoomJoinMessage
            {
                PlayerName = MiSideCoopPlugin.LocalPlayerName.Value,
                PlayerRole = 2
            });

            SpawnPlayer2Local();
            SpawnPlayer1Remote();
        }

        private void Client_OnDisconnected()
        {
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Déconnecté de l'hôte.");
            _isClient = false;
            DestroyRemoteAvatars();
        }

        private void Client_OnPlayerState(PlayerStateMessage msg)
        {
            // Le client reçoit l'état de l'hôte → l'applique sur le Player1 distant
            _player1?.ApplyRemoteState(msg);
        }

        private void Client_OnPlayerAction(PlayerActionMessage msg)
        {
            // Applique l'animation sur l'avatar distant correspondant
            var remoteAvatar = _isHost ? (MonoBehaviour)_player2 : _player1;
            remoteAvatar?.GetComponent<AvatarAnimatorSync>()?.PlayActionAnimation(msg.ActionType);

            if (msg.ActionType == "PickUp" && !string.IsNullOrEmpty(msg.TargetObjectId))
            {
                var obj = GameObject.Find(msg.TargetObjectId);
                if (obj != null) obj.SetActive(false);
            }
        }

        private void Client_OnSceneChange(SceneChangeMessage msg)
        {
            MiSideCoopPlugin.Logger.LogInfo($"[Co-op] Changement de scène vers '{msg.SceneName}'.");
            SceneManager.LoadScene(msg.SceneName);
        }

        private void Client_OnCutscene(CutsceneMessage msg)
        {
            GameStateSync.Instance?.TriggerCutscene(msg.CutsceneId, msg.Start);
        }

        private void Client_OnObjectSync(ObjectSyncMessage msg)
        {
            var obj = GameObject.Find(msg.ObjectId);
            if (obj == null) return;
            obj.SetActive(msg.IsActive);
            if (msg.IsActive)
                obj.transform.position = msg.Position;
        }

        // ── Gestion des avatars ───────────────────────────────────────────────

        /// <summary>Hôte : initialise son propre avatar (MC du jeu).</summary>
        private void SpawnPlayer1Local()
        {
            var mcGo = FindMCGameObject() ?? CreateDefaultHumanoid("Player1_MC");
            _player1 = mcGo.GetComponent<Player1Avatar>()
                    ?? mcGo.AddComponent<Player1Avatar>();
            _player1.Initialize(MiSideCoopPlugin.LocalPlayerName.Value, true);
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Avatar hôte (Player1) initialisé.");
        }

        /// <summary>Hôte : crée le mannequin distant de l'invité.</summary>
        private void SpawnPlayer2Remote()
        {
            var go = CreateDefaultHumanoid("Player2_Guest");
            _player2 = go.AddComponent<Player2Avatar>();
            _player2.Initialize(ConnectedPlayerName, false);
            _player2.ApplySkinColor(MiSideCoopPlugin.Player2SkinColor.Value);
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Avatar invité (Player2) instancié côté hôte.");
        }

        /// <summary>Client : initialise son propre avatar (Player2).</summary>
        private void SpawnPlayer2Local()
        {
            var go = CreateDefaultHumanoid("Player2_Self");
            _player2 = go.AddComponent<Player2Avatar>();
            _player2.Initialize(MiSideCoopPlugin.LocalPlayerName.Value, true);
            _player2.ApplySkinColor(MiSideCoopPlugin.Player2SkinColor.Value);
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Avatar local (Player2) initialisé côté client.");
        }

        /// <summary>Client : crée le mannequin distant de l'hôte (Player1).</summary>
        private void SpawnPlayer1Remote()
        {
            var go = FindMCGameObject() ?? CreateDefaultHumanoid("Player1_Remote");
            _player1 = go.GetComponent<Player1Avatar>() ?? go.AddComponent<Player1Avatar>();
            _player1.Initialize("Hôte", false);
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Représentation distante du MC (Player1) initialisée.");
        }

        private void DestroyRemoteAvatars()
        {
            // Côté hôte : le Player2 est toujours une instance réseau à détruire
            if (_player2 != null && !_player2.IsLocalPlayer)
            {
                Destroy(_player2.gameObject);
                _player2 = null;
            }
            // Côté client : le Player1 distant est une copie à détruire
            if (_player1 != null && !_player1.IsLocalPlayer)
            {
                Destroy(_player1.gameObject);
                _player1 = null;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static GameObject FindMCGameObject()
        {
            string[] names = { "MC", "Player", "PlayerController", "MainCharacter", "PlayerCharacter", "male_mc" };
            foreach (var n in names)
            {
                var go = GameObject.Find(n);
                if (go != null) return go;
            }
            return GameObject.FindWithTag("Player");
        }

        private static GameObject CreateDefaultHumanoid(string goName)
        {
            var root = new GameObject(goName);

            // Corps (capsule)
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.transform.SetParent(root.transform);
            body.transform.localPosition = new Vector3(0, 1f, 0);
            body.transform.localScale    = new Vector3(0.4f, 0.9f, 0.4f);
            Object.Destroy(body.GetComponent<CapsuleCollider>());

            // Tête
            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.transform.SetParent(root.transform);
            head.transform.localPosition = new Vector3(0, 2.1f, 0);
            head.transform.localScale    = Vector3.one * 0.35f;
            Object.Destroy(head.GetComponent<SphereCollider>());

            // Collider de l'avatar (Trigger uniquement)
            var col = root.AddComponent<CapsuleCollider>();
            col.height  = 2f;
            col.center  = new Vector3(0, 1f, 0);
            col.isTrigger = true;   // ne bloque jamais la physique locale

            return root;
        }

        private static string SampleAnimatorState(Animator anim)
        {
            if (anim == null) return "Idle";
            var info = anim.GetCurrentAnimatorStateInfo(0);
            if (info.IsName("Walk")    || info.IsName("Walking"))  return "Walk";
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

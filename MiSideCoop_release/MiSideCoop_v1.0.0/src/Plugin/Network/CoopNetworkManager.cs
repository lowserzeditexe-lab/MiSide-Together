using System;
using System.IO;
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
        }

        private void OnDestroy() => StopCoop();

        // ── API publique ──────────────────────────────────────────────────────

        public void StartHost(string roomCode)
        {
            RoomCode = roomCode;
            _isHost  = true;
            _tx.StartHost(MiSideCoopPlugin.NetworkPort.Value);
            MiSideCoopPlugin.Logger.LogInfo(
                $"[Co-op] Serveur démarré. Port {MiSideCoopPlugin.NetworkPort.Value}. Code : {roomCode}");
            SpawnPlayer1Local();
        }

        public void StartClient(string hostIp, int port)
        {
            _isClient = true;
            _tx.ConnectClient(hostIp, port);
            MiSideCoopPlugin.Logger.LogInfo($"[Co-op] Connexion vers {hostIp}:{port}…");
        }

        public void StopCoop()
        {
            _tx?.Stop();
            _isHost = false;
            _isClient = false;
            DestroyRemoteAvatars();
            RoomCode = null;
            ConnectedPlayerName = "...";
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Session co-op terminée.");
        }

        // ── Broadcasts publics (appelés depuis les Patches) ───────────────────
        public void BroadcastAction(PlayerActionMessage msg)     => _tx.Send(msg);
        public void BroadcastObjectSync(ObjectSyncMessage msg)   => _tx.Send(msg);
        public void BroadcastSceneChange(SceneChangeMessage msg) { if (_isHost) _tx.Send(msg); }
        public void BroadcastCutsceneMessage(CutsceneMessage msg){ if (_isHost) _tx.Send(msg); }

        // ── Update : envoi de l'état local + pump des messages entrants ───────
        private void Update()
        {
            if (!IsConnected) return;

            // 1) Dispatch des messages reçus (thread Unity)
            while (_tx.Incoming.TryDequeue(out var item))
                Dispatch(item.id, item.payload);

            // 2) Envoi de l'état local à cadence fixe
            _syncTimer += Time.deltaTime;
            if (_syncTimer < SyncInterval) return;
            _syncTimer = 0f;
            SendLocalState();
        }

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
            _tx.Send(msg);
        }

        // ── Handlers de connexion ────────────────────────────────────────────
        private void OnRemotePeerConnected()
        {
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Pair distant connecté.");
            if (_isHost) SpawnPlayer2Remote();
        }

        private void OnRemotePeerDisconnected()
        {
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Pair distant déconnecté.");
            DestroyRemoteAvatars();
        }

        private void OnConnectedToHost()
        {
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Connexion à l'hôte réussie.");
            _tx.Send(new RoomJoinMessage
            {
                PlayerName = MiSideCoopPlugin.LocalPlayerName.Value,
                PlayerRole = 2
            });
            SpawnPlayer2Local();
            SpawnPlayer1Remote();
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
            MiSideCoopPlugin.Logger.LogInfo($"[Co-op] Pair identifié : {msg.PlayerName}");
        }

        private void OnSceneChange(SceneChangeMessage msg)
        {
            if (_isHost) return; // l'hôte initie, le client suit
            MiSideCoopPlugin.Logger.LogInfo($"[Co-op] Changement de scène vers '{msg.SceneName}'.");
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
        private void SpawnPlayer1Local()
        {
            var mcGo = FindMCGameObject() ?? CreateDefaultHumanoid("Player1_MC");
            _player1 = mcGo.GetComponent<Player1Avatar>() ?? mcGo.AddComponent<Player1Avatar>();
            _player1.Initialize(MiSideCoopPlugin.LocalPlayerName.Value, true);
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Avatar hôte (Player1) initialisé.");
        }

        private void SpawnPlayer2Remote()
        {
            var go = CreateDefaultHumanoid("Player2_Guest");
            _player2 = go.AddComponent<Player2Avatar>();
            _player2.Initialize(ConnectedPlayerName, false);
            _player2.ApplySkinColor(MiSideCoopPlugin.Player2SkinColor.Value);
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Avatar invité (Player2) instancié côté hôte.");
        }

        private void SpawnPlayer2Local()
        {
            var go = CreateDefaultHumanoid("Player2_Self");
            _player2 = go.AddComponent<Player2Avatar>();
            _player2.Initialize(MiSideCoopPlugin.LocalPlayerName.Value, true);
            _player2.ApplySkinColor(MiSideCoopPlugin.Player2SkinColor.Value);
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Avatar local (Player2) initialisé côté client.");
        }

        private void SpawnPlayer1Remote()
        {
            var go = FindMCGameObject() ?? CreateDefaultHumanoid("Player1_Remote");
            _player1 = go.GetComponent<Player1Avatar>() ?? go.AddComponent<Player1Avatar>();
            _player1.Initialize("Hôte", false);
            MiSideCoopPlugin.Logger.LogInfo("[Co-op] Représentation distante du MC initialisée.");
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
        private static GameObject FindMCGameObject()
        {
            // MiSide : le MC porte le composant PlayerMove (cf. dump IL2CPP).
            // Résolution à runtime via réflexion (pas de référence directe au type).
            var playerMoveType = HarmonyLib.AccessTools.TypeByName("PlayerMove");
            if (playerMoveType != null)
            {
                var found = UnityEngine.Object.FindObjectOfType(playerMoveType) as Component;
                if (found != null) return found.gameObject;
            }

            string[] names = { "MC", "Player", "PlayerController", "MainCharacter", "PlayerCharacter", "male_mc", "Person" };
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

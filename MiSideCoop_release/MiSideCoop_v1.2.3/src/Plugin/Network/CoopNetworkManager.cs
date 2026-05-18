using System;
using System.IO;
using HarmonyLib;
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
            _tx.StartHost(MiSideCoopPlugin.NetworkPort.Value);
            MiSideCoopPlugin.Logger.LogInfo(
                $"[Co-op] Server started. Port {MiSideCoopPlugin.NetworkPort.Value}. Code: {roomCode}");
            SpawnPlayer1Local();
        }

        public void StartClient(string hostIp, int port)
        {
            _isClient = true;
            _tx.ConnectClient(hostIp, port);
            MiSideCoopPlugin.Logger.LogInfo($"[Co-op] Connecting to {hostIp}:{port}…");
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
                    // Le spawn sera ré-essayé via ScenePatch / GameStateSync à la
                    // prochaine transition vers une scène contenant PlayerMove.
                    MiSideCoopPlugin.Logger.LogInfo(
                        "[Co-op] Host avatar spawn deferred — no PlayerMove in current scene " +
                        "(probably still in main menu). Will retry on next scene load.");
                    return;
                }
                _player1 = mcGo.GetComponent<Player1Avatar>() ?? mcGo.AddComponent<Player1Avatar>();
                _player1.Initialize(MiSideCoopPlugin.LocalPlayerName.Value, true);
                MiSideCoopPlugin.Logger.LogInfo("[Co-op] Host avatar (Player1) initialized.");
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
                _player2 = go.AddComponent<Player2Avatar>();
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
                _player2 = go.AddComponent<Player2Avatar>();
                _player2.Initialize(MiSideCoopPlugin.LocalPlayerName.Value, true);
                _player2.ApplySkinColor(MiSideCoopPlugin.Player2SkinColor.Value);
                MiSideCoopPlugin.Logger.LogInfo("[Co-op] Local avatar (Player2) initialized on client.");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger.LogError($"[Co-op] SpawnPlayer2Local failed: {ex.Message}");
            }
        }

        private void SpawnPlayer1Remote()
        {
            try
            {
                var go = FindMCGameObject();
                if (go == null)
                {
                    // Pas de PlayerMove → on est encore au menu côté client.
                    // On attend la sync de scène pour respawn.
                    MiSideCoopPlugin.Logger.LogInfo(
                        "[Co-op] Remote Player1 spawn deferred — no PlayerMove yet.");
                    return;
                }
                _player1 = go.GetComponent<Player1Avatar>() ?? go.AddComponent<Player1Avatar>();
                _player1.Initialize("Host", false);
                MiSideCoopPlugin.Logger.LogInfo("[Co-op] Remote MC representation initialized.");
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
            // ── v1.2.3 FIX MITA ──
            // Méthode robuste #1 : trouver le GameObject qui PORTE le composant
            // PlayerMove (le vrai contrôleur du MC dans MiSide, confirmé par les
            // Harmony patches existantes). C'est unique et sans ambiguïté :
            // Mita n'a pas PlayerMove, donc impossible de se tromper de cible.
            var pmGo = FindGameObjectByComponentTypeName("PlayerMove");
            if (pmGo != null) return pmGo;

            // Méthode #2 : noms spécifiques au MC (volontairement RESTREINT —
            // "Person" et "Player" retirés en v1.2.3 car ils matchaient Mita
            // dans certaines scènes et faisaient buguer le menu).
            string[] names = { "male_mc", "MC", "PlayerCharacter", "MainCharacter", "PlayerController" };
            foreach (var n in names)
            {
                var go = SafeGameObjectFind(n);
                if (go != null) return go;
            }

            // Plus de fallback générique FindWithTag("Player") : trop risqué
            // (peut matcher n'importe quel GO avec tag Player, y compris Mita).
            return null;
        }

        /// <summary>
        /// Résout un type IL2CPP par nom via Harmony AccessTools puis cherche
        /// la 1ère instance MonoBehaviour de ce type dans la scène et retourne
        /// son GameObject. Filet de sécurité try/catch pour les éventuelles APIs
        /// IL2CPP strippées.
        /// </summary>
        private static GameObject FindGameObjectByComponentTypeName(string typeName)
        {
            try
            {
                var t = AccessTools.TypeByName(typeName);
                if (t == null) return null;
                var found = UnityEngine.Object.FindObjectOfType(t) as Component;
                return found?.gameObject;
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] FindGameObjectByComponentTypeName('{typeName}'): {ex.Message}");
                return null;
            }
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

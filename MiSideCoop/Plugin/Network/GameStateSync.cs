using System;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideCoop.Network
{
    /// <summary>
    /// Synchronise les événements globaux entre hôte et invité :
    /// changements de scène, déclenchement/fin de cutscenes.
    /// </summary>
    public class GameStateSync : MonoBehaviour
    {
        public GameStateSync(IntPtr ptr) : base(ptr) { }

        public static GameStateSync Instance { get; private set; }

        private string _currentScene;

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            _currentScene = SceneManager.GetActiveScene().name;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        // ── Changement de scène (déclenché côté hôte) ─────────────────────────
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var nm = CoopNetworkManager.Instance;
            if (nm == null || !nm.IsHost) return;
            if (scene.name == _currentScene) return;

            _currentScene = scene.name;

            var spawnPoint = GameObject.Find("SpawnPoint") ?? GameObject.Find("PlayerSpawn");
            Vector3 spawnPos = spawnPoint != null ? spawnPoint.transform.position : Vector3.zero;

            nm.BroadcastSceneChange(new SceneChangeMessage
            {
                SceneName     = scene.name,
                SpawnPosition = spawnPos
            });

            MiSideCoopPlugin.Logger.LogInfo($"[Co-op] Scène '{scene.name}' synchronisée avec l'invité.");
        }

        // ── Cutscenes ─────────────────────────────────────────────────────────

        /// <summary>Appelé depuis un patch Harmony quand une cutscene démarre (côté hôte).</summary>
        public void BroadcastCutscene(string cutsceneId, bool start)
        {
            CoopNetworkManager.Instance?.BroadcastCutsceneMessage(new CutsceneMessage
            {
                CutsceneId = cutsceneId,
                Start      = start
            });
        }

        /// <summary>Reçu côté client : applique la cutscene localement.</summary>
        public void TriggerCutscene(string cutsceneId, bool start)
        {
            var obj = GameObject.Find(cutsceneId);
            if (obj == null) return;

            var anim = obj.GetComponent<Animator>();
            if (anim != null)
                anim.Play(start ? "CutsceneStart" : "Idle");

            MiSideCoopPlugin.Logger.LogInfo(
                $"[Co-op] Cutscène '{cutsceneId}' : {(start ? "démarrée" : "terminée")}.");
        }
    }
}

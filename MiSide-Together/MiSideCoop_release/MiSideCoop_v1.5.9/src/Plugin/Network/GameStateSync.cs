using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideCoop.Network
{
    /// <summary>
    /// Synchronise les événements globaux entre hôte et invité :
    /// changements de scène, déclenchement/fin de cutscenes.
    ///
    /// Note IL2CPP : on n'utilise PAS SceneManager.sceneLoaded (UnityAction
    /// strippé), on relit le nom de scène à chaque frame dans Update().
    /// </summary>
    public class GameStateSync : MonoBehaviour
    {
        public static GameStateSync Instance { get; private set; }

        private string _currentScene = string.Empty;
        private float _checkTimer;
        private const float CheckInterval = 0.5f;

        private void Awake()
        {
            if (Instance != null) { Destroy(this); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Start()
        {
            try { _currentScene = SceneManager.GetActiveScene().name; }
            catch { _currentScene = string.Empty; }
        }

        private void Update()
        {
            _checkTimer += Time.deltaTime;
            if (_checkTimer < CheckInterval) return;
            _checkTimer = 0f;

            string sceneName;
            try { sceneName = SceneManager.GetActiveScene().name; }
            catch { return; }

            if (sceneName == _currentScene) return;
            _currentScene = sceneName;
            OnSceneChanged(sceneName);
        }

        private void OnSceneChanged(string sceneName)
        {
            var nm = CoopNetworkManager.Instance;
            if (nm == null || !nm.IsHost) return;

            Vector3 spawnPos = Vector3.zero;
            var spawnPoint = GameObject.Find("SpawnPoint") ?? GameObject.Find("PlayerSpawn");
            if (spawnPoint != null) spawnPos = spawnPoint.transform.position;

            nm.BroadcastSceneChange(new SceneChangeMessage
            {
                SceneName     = sceneName,
                SpawnPosition = spawnPos
            });

            // v1.3.10 — Le log précédent disait "synced with guest" même quand
            // aucun guest n'était connecté, ce qui était trompeur en debug
            // (logs v1.3.9 : le host affichait "Scene synced with guest" alors
            // qu'aucun "Remote peer connected" n'avait jamais eu lieu, parce
            // que le guest avait typo le room code et était en fait dans une
            // autre room).
            bool hasPeer = !string.IsNullOrEmpty(nm.ConnectedPlayerName)
                          && nm.ConnectedPlayerName != "...";
            MiSideCoopPlugin.Logger.LogInfo(hasPeer
                ? $"[Co-op] Scene '{sceneName}' broadcasted to guest."
                : $"[Co-op] Scene '{sceneName}' broadcast (no peer connected).");
        }

        // ── Cutscenes ─────────────────────────────────────────────────────────

        public void BroadcastCutscene(string cutsceneId, bool start)
        {
            CoopNetworkManager.Instance?.BroadcastCutsceneMessage(new CutsceneMessage
            {
                CutsceneId = cutsceneId,
                Start      = start
            });
        }

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

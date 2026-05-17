using System;
using UnityEngine;
using MiSideCoop.Network;
using MiSideCoop.UI;
using MiSideCoop.Relay;

namespace MiSideCoop
{
    /// <summary>
    /// MonoBehaviour créé au démarrage du plugin. Attache tous les composants
    /// co-op sur un objet persistant (DontDestroyOnLoad).
    /// </summary>
    public class CoopBootstrap : MonoBehaviour
    {

        private CoopMenuUI         _menuUI;
        private CoopNetworkManager _networkManager;

        private void Awake()
        {
            _networkManager = gameObject.AddComponent<CoopNetworkManager>();
            _menuUI         = gameObject.AddComponent<CoopMenuUI>();
            gameObject.AddComponent<GameStateSync>();
            gameObject.AddComponent<RoomManager>();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
                _menuUI?.ToggleMenu();
        }
    }
}

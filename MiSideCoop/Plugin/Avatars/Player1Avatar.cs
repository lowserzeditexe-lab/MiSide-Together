using System;
using UnityEngine;
using MiSideCoop.Network;

namespace MiSideCoop.Avatars
{
    /// <summary>
    /// Avatar du Joueur 1 (hôte — MC original du jeu).
    ///
    /// Côté hôte  : IsLocalPlayer = true  → script de contrôle intact, caméra active.
    /// Côté invité : IsLocalPlayer = false → scripts de contrôle désactivés, Rigidbody kinematic,
    ///               tag flottant visible au-dessus de la tête.
    /// </summary>
    public class Player1Avatar : PlayerAvatar
    {
        public Player1Avatar(IntPtr ptr) : base(ptr) { }

        private GameObject _nameTag;

        public override void Initialize(string playerName, bool isLocal)
        {
            base.Initialize(playerName, isLocal);

            if (isLocal)
            {
                EnsureCameraActive();
            }
            else
            {
                DisableLocalInputScripts();
                MakeKinematic();
                CreateNameTag();
            }

            MiSideCoopPlugin.Logger.LogInfo(
                $"[Co-op] Player1Avatar '{playerName}' prêt (local={isLocal}).");
        }

        // ── Désactivation des scripts de contrôle (avatar distant) ────────────
        private void DisableLocalInputScripts()
        {
            foreach (var comp in GetComponents<MonoBehaviour>())
            {
                if (comp == this) continue;
                var typeName = comp.GetIl2CppType().FullName;
                if (typeName.Contains("Controller") ||
                    typeName.Contains("Movement")   ||
                    typeName.Contains("Input")      ||
                    typeName.Contains("PlayerAnim"))
                {
                    comp.enabled = false;
                }
            }
        }

        private void MakeKinematic()
        {
            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.useGravity  = false;
            }

            // Collider en trigger uniquement côté distant
            var col = GetComponent<Collider>();
            if (col != null) col.isTrigger = true;
        }

        // ── Tag flottant ──────────────────────────────────────────────────────
        private void CreateNameTag()
        {
            _nameTag = new GameObject("P1_NameTag");
            _nameTag.transform.SetParent(transform);
            _nameTag.transform.localPosition = Vector3.up * 2.6f;

            var tm            = _nameTag.AddComponent<TextMesh>();
            tm.text           = PlayerName;
            tm.color          = Color.white;
            tm.fontSize       = 22;
            tm.alignment      = TextAlignment.Center;
            tm.anchor         = TextAnchor.MiddleCenter;
            tm.characterSize  = 0.08f;
        }

        // ── Caméra locale ─────────────────────────────────────────────────────
        private void EnsureCameraActive()
        {
            var cam = GetComponentInChildren<Camera>();
            if (cam != null) cam.enabled = true;
        }

        protected override void Update()
        {
            base.Update(); // interpolation réseau si distant

            if (_nameTag != null && Camera.main != null)
            {
                _nameTag.transform.LookAt(
                    _nameTag.transform.position + Camera.main.transform.rotation * Vector3.forward,
                    Camera.main.transform.rotation * Vector3.up);
            }
        }
    }
}

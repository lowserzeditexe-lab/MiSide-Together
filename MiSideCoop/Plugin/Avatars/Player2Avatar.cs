using System;
using UnityEngine;
using MiSideCoop.Network;

namespace MiSideCoop.Avatars
{
    /// <summary>
    /// Avatar du Joueur 2 (invité — second personnage distinct).
    ///
    /// Côté invité : IsLocalPlayer = true  → caméra dédiée créée, collider Trigger, skin coloré.
    /// Côté hôte   : IsLocalPlayer = false → Rigidbody kinematic, tag flottant visible.
    ///
    /// La couleur du skin est configurable via le fichier de config BepInEx
    /// (clé : Avatar > Player2SkinColor).
    /// </summary>
    public class Player2Avatar : PlayerAvatar
    {
        public Player2Avatar(IntPtr ptr) : base(ptr) { }

        private Camera     _ownCamera;
        private GameObject _nameTag;
        private Renderer[] _renderers;

        public override void Initialize(string playerName, bool isLocal)
        {
            base.Initialize(playerName, isLocal);

            _renderers = GetComponentsInChildren<Renderer>();

            if (isLocal)
            {
                SetupOwnCamera();
                SetupTriggerCollider();
            }
            else
            {
                MakeKinematic();
            }

            CreateNameTag();
            ApplySkinColor(MiSideCoopPlugin.Player2SkinColor.Value);

            MiSideCoopPlugin.Logger.LogInfo(
                $"[Co-op] Player2Avatar '{playerName}' prêt (local={isLocal}).");
        }

        // ── Couleur du skin ───────────────────────────────────────────────────

        public void ApplySkinColor(string colorName)
        {
            _renderers = GetComponentsInChildren<Renderer>();
            var color  = ParseColor(colorName);

            foreach (var rend in _renderers)
            {
                if (rend == null || rend.sharedMaterial == null) continue;
                var mat = rend.material; // instance individuelle
                if (mat.HasProperty("_Color"))     mat.SetColor("_Color",     color);
                if (mat.HasProperty("_BaseColor"))  mat.SetColor("_BaseColor", color);
            }
        }

        // ── Caméra dédiée (côté invité) ───────────────────────────────────────
        private void SetupOwnCamera()
        {
            // Désactive toutes les caméras existantes
            foreach (var cam in Camera.allCameras)
                cam.enabled = false;

            var camGo = new GameObject("Player2Camera");
            camGo.transform.SetParent(transform);
            camGo.transform.localPosition = new Vector3(0, 1.65f, -0.25f);
            camGo.transform.localRotation = Quaternion.identity;

            _ownCamera             = camGo.AddComponent<Camera>();
            _ownCamera.tag         = "MainCamera";
            _ownCamera.nearClipPlane = 0.01f;
            _ownCamera.fieldOfView  = 75f;
            _ownCamera.enabled      = true;
        }

        // ── Collider Trigger (ne bloque jamais la physique locale) ────────────
        private void SetupTriggerCollider()
        {
            var col = GetComponent<CapsuleCollider>() ?? gameObject.AddComponent<CapsuleCollider>();
            col.height    = 2f;
            col.center    = new Vector3(0, 1f, 0);
            col.isTrigger = true;
        }

        // ── Kinematic pour avatar distant ─────────────────────────────────────
        private void MakeKinematic()
        {
            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.useGravity  = false;
            }
            var col = GetComponent<Collider>();
            if (col != null) col.isTrigger = true;
        }

        // ── Tag pseudo flottant ───────────────────────────────────────────────
        private void CreateNameTag()
        {
            _nameTag = new GameObject("P2_NameTag");
            _nameTag.transform.SetParent(transform);
            _nameTag.transform.localPosition = Vector3.up * 2.4f;

            var tm            = _nameTag.AddComponent<TextMesh>();
            tm.text           = PlayerName;
            tm.color          = new Color(0.3f, 0.8f, 1f); // cyan clair
            tm.fontSize       = 22;
            tm.alignment      = TextAlignment.Center;
            tm.anchor         = TextAnchor.MiddleCenter;
            tm.characterSize  = 0.08f;
        }

        protected override void Update()
        {
            base.Update();

            if (_nameTag != null && Camera.main != null)
            {
                _nameTag.transform.LookAt(
                    _nameTag.transform.position + Camera.main.transform.rotation * Vector3.forward,
                    Camera.main.transform.rotation * Vector3.up);
            }
        }

        // ── Parsing couleur ───────────────────────────────────────────────────
        private static Color ParseColor(string name) => name?.ToLowerInvariant() switch
        {
            "red"    => new Color(0.85f, 0.20f, 0.20f),
            "blue"   => new Color(0.20f, 0.45f, 0.90f),
            "green"  => new Color(0.20f, 0.72f, 0.30f),
            "yellow" => new Color(0.95f, 0.82f, 0.10f),
            "purple" => new Color(0.62f, 0.18f, 0.82f),
            "orange" => new Color(0.95f, 0.52f, 0.10f),
            "white"  => Color.white,
            "black"  => new Color(0.15f, 0.15f, 0.15f),
            _        => new Color(0.20f, 0.45f, 0.90f)   // blue par défaut
        };
    }
}

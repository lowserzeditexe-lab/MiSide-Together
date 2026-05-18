using System;
using UnityEngine;
using UnityEngine.SceneManagement;
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

        private Camera     _ownCamera;
        private GameObject _nameTag;
        private Renderer[] _renderers;

        public override void Initialize(string playerName, bool isLocal)
        {
            // ── v1.3.5 STEP-BY-STEP TRY/CATCH ──
            // En v1.3.3 on englobait toute l'init dans un seul try/catch. Mais
            // si une étape PRÉCOCE (SetupOwnCamera, à cause de Camera.allCameras
            // strippé) levait, les étapes suivantes (CreateNameTag, ApplySkinColor)
            // étaient skippées. Résultat côté guest : pas de caméra dédiée et pas
            // de couleur, donc rien à voir.
            //
            // v1.3.5 : chaque étape a son propre try/catch — un fail isolé ne
            // bloque plus les étapes suivantes. base.Initialize reste critique
            // (établit PlayerName/IsLocalPlayer), donc il porte aussi son try/catch.
            try { base.Initialize(playerName, isLocal); }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogError(
                    $"[Co-op] Player2Avatar.base.Initialize failed: {ex.Message}");
            }

            try { _renderers = this.GetComponentsInChildrenSafe<Renderer>(); }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] Player2Avatar renderer scan failed: {ex.Message}");
                _renderers = Array.Empty<Renderer>();
            }

            if (isLocal)
            {
                try { SetupOwnCamera(); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogError(
                        $"[Co-op] Player2Avatar.SetupOwnCamera failed: {ex.Message}");
                }
                try { SetupTriggerCollider(); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        $"[Co-op] Player2Avatar.SetupTriggerCollider failed: {ex.Message}");
                }
            }
            else
            {
                try { MakeKinematic(); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        $"[Co-op] Player2Avatar.MakeKinematic failed: {ex.Message}");
                }
            }

            try { CreateNameTag(); }
            catch (Exception ex)
            {
                // Cosmétique — non bloquant. Log silencieux pour ne pas spammer.
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] Player2Avatar.CreateNameTag skipped (IL2CPP stripped): {ex.Message}");
            }

            try { ApplySkinColor(MiSideCoopPlugin.Player2SkinColor.Value); }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] Player2Avatar.ApplySkinColor failed: {ex.Message}");
            }

            MiSideCoopPlugin.Logger?.LogInfo(
                $"[Co-op] Player2Avatar '{playerName}' ready (local={isLocal}).");
        }

        // ── Couleur du skin ───────────────────────────────────────────────────

        public void ApplySkinColor(string colorName)
        {
            _renderers = this.GetComponentsInChildrenSafe<Renderer>();
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
            // v1.3.5 — Camera.allCameras est strippé en IL2CPP MiSide
            // ("Method not found: 'UnityEngine.Camera[] UnityEngine.Camera.get_allCameras()'").
            // On scanne la scène active à la place, via SceneManager.GetActiveScene()
            // et notre traversal manuel (GetComponentsInChildrenSafe) qui n'utilise
            // que des primitives non-strippées (Transform.childCount/GetChild +
            // GetComponent<Camera>() singulier générique, prouvé fonctionnel).
            DisableExistingCameras();

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

        /// <summary>
        /// Désactive toutes les caméras visibles de la scène active (remplace
        /// <c>Camera.allCameras</c> ET <c>Scene.GetRootGameObjects()</c>, tous
        /// deux strippés en IL2CPP MiSide 0.93L). Délégué à
        /// <see cref="SceneDiagnostics.DisableExistingCamerasSafe"/> qui n'utilise
        /// que <c>Camera.main</c> + traversal manuel via Transform.childCount.
        /// </summary>
        private static void DisableExistingCameras()
        {
            SceneDiagnostics.DisableExistingCamerasSafe();
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
            // v1.3.7 — TextMesh est CONFIRMÉ absent du build IL2CPP MiSide 0.93L
            // (logs v1.3.6 sur Player1 ET Player2 : reflection path + generic
            // fallback échouent tous deux, "type initializer for
            // MethodInfoStoreGeneric_AddComponent_Public_T_0`1 threw").
            // → MiSide utilise TextMeshPro (TMP). TextMesh n'existe simplement
            //   pas dans son assembly IL2CPP. Tenter AddComponent<TextMesh>
            //   inonde le log à chaque spawn sans résultat.
            //
            // → On désactive la création du nametag jusqu'à ce qu'on câble
            //   le mod sur TMPro.TextMeshPro (nécessite la référence assembly
            //   TMP_Essentials, à ajouter au csproj plus tard). Le nametag est
            //   purement cosmétique donc son absence ne bloque rien.
            _nameTag = null;
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

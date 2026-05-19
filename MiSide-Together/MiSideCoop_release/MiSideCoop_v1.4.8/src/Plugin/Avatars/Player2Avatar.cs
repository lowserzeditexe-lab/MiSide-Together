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
            // v1.4.1 — PIVOT ARCHITECTURAL :
            //
            // Avant v1.4.1 : sur le guest, on créait un GameObject synthétique
            // 'Player2_Self' (capsule + sphère) auquel on attachait Player2Avatar
            // avec isLocal=true. On y installait notre propre caméra taguée
            // MainCamera, un trigger collider, etc. → Conflit avec le MC MiSide
            // local du guest (qui a aussi sa propre MainCamera) → NRE en cascade
            // au load de Scene 1 - RealRoom, screen gris-bleu uni.
            //
            // Depuis v1.4.1 : chaque joueur joue MiSide normalement sur sa
            // machine avec son propre MC 'Player' local. Player2Avatar est
            // attaché DIRECTEMENT SUR LE MC RÉEL (comme Player1Avatar côté
            // host). Le mode "remote" (isLocal=false) reste sur un humanoïde
            // synthétique pour visualiser le peer côté host.
            //
            // → Côté guest, isLocal=true signifie : "Player2Avatar est attaché
            //   sur le vrai MC du guest, qui sert juste à streamer position +
            //   rotation + état Animator au host". On NE TOUCHE PAS au MC :
            //   pas de SetupOwnCamera (MainCamera conflict), pas de trigger
            //   collider (remplacerait le CharacterController du MC), pas
            //   d'ApplySkinColor (recolorisrait les textures du MC), pas de
            //   MakeKinematic (figerait le MC). Le streaming de la position
            //   se fait via PlayerAvatar.transform qui pointe directement sur
            //   le transform du MC, donc tout est gratuit.
            //
            // → Côté host, isLocal=false signifie : "Player2Avatar est attaché
            //   sur l'humanoïde synthétique Player2_Guest qui représente
            //   visuellement le guest". On garde toute la mise en scène
            //   (kinematic, nametag, skin color) — c'est notre propre objet.

            try { base.Initialize(playerName, isLocal); }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogError(
                    $"[Co-op] Player2Avatar.base.Initialize failed: {ex.Message}");
            }

            if (isLocal)
            {
                // Attaché sur le VRAI MC MiSide local → aucune modification
                // destructive. Le rôle de cet avatar est purement de streamer
                // la position via PlayerAvatar.transform (= transform du MC).
                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] Player2Avatar '{playerName}' attached on local MC (read-only stream).");
                return;
            }

            // ── isLocal=false : ghost du peer côté host ──
            //
            // v1.4.8 — Détecte automatiquement si on est attaché à :
            //   • un vrai clone 3D (RealMcCloner) → on a un SkinnedMeshRenderer
            //     dans la hiérarchie (le mesh skinné du MC MiSide). Dans ce cas
            //     on PRÉSERVE les matériaux/textures originaux et on saute
            //     ApplySkinColor (sinon on repeint tout en bleu uniforme et le
            //     modèle ressemble à un "bonhomme bleu").
            //   • une capsule + sphère synthétique (fallback CreateDefaultHumanoid)
            //     → pas de SkinnedMeshRenderer. On garde l'ancien comportement
            //     ApplySkinColor pour différencier host/guest visuellement.
            try { _renderers = this.GetComponentsInChildrenSafe<Renderer>(); }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] Player2Avatar renderer scan failed: {ex.Message}");
                _renderers = Array.Empty<Renderer>();
            }

            bool isClone3D = HasSkinnedMeshRenderer();

            try { MakeKinematic(); }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] Player2Avatar.MakeKinematic failed: {ex.Message}");
            }

            try { CreateNameTag(); }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] Player2Avatar.CreateNameTag skipped (IL2CPP stripped): {ex.Message}");
            }

            if (!isClone3D)
            {
                try { ApplySkinColor(MiSideCoopPlugin.Player2SkinColor.Value); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        $"[Co-op] Player2Avatar.ApplySkinColor failed: {ex.Message}");
                }
            }

            MiSideCoopPlugin.Logger?.LogInfo(
                $"[Co-op] Player2Avatar '{playerName}' ready (remote ghost on host, "
              + $"mode={(isClone3D ? "3D-clone, original textures" : "synthetic, blue-tinted")}).");
        }

        /// <summary>
        /// v1.4.8 — Détecte si l'avatar a au moins un SkinnedMeshRenderer.
        /// Présence = c'est un clone 3D du MC MiSide (mesh skinné Mita).
        /// Absence = c'est une capsule + sphère synthétique (CreateDefaultHumanoid).
        /// </summary>
        private bool HasSkinnedMeshRenderer()
        {
            try
            {
                if (_renderers == null) return false;
                foreach (var r in _renderers)
                {
                    if (r is SkinnedMeshRenderer) return true;
                }
                return false;
            }
            catch { return false; }
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

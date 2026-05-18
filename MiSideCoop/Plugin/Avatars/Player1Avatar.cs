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

        private GameObject _nameTag;

        public override void Initialize(string playerName, bool isLocal)
        {
            // ── v1.3.5 STEP-BY-STEP TRY/CATCH ──
            // Chaque étape dans son propre try/catch pour qu'un fail IL2CPP
            // ciblé (ex: AddComponent<TextMesh> dans CreateNameTag) n'empêche
            // pas les autres étapes (camera, kinematic, etc.).
            try { base.Initialize(playerName, isLocal); }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogError(
                    $"[Co-op] Player1Avatar.base.Initialize failed: {ex.Message}");
            }

            if (isLocal)
            {
                try { EnsureCameraActive(); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        $"[Co-op] Player1Avatar.EnsureCameraActive failed: {ex.Message}");
                }
            }
            else
            {
                try { DisableLocalInputScripts(); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        $"[Co-op] Player1Avatar.DisableLocalInputScripts failed: {ex.Message}");
                }
                try { MakeKinematic(); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        $"[Co-op] Player1Avatar.MakeKinematic failed: {ex.Message}");
                }
                try { CreateNameTag(); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        $"[Co-op] Player1Avatar.CreateNameTag skipped (IL2CPP stripped): {ex.Message}");
                }
            }

            MiSideCoopPlugin.Logger?.LogInfo(
                $"[Co-op] Player1Avatar '{playerName}' ready (local={isLocal}).");
        }

        // ── Désactivation des scripts de contrôle (avatar distant) ────────────
        private void DisableLocalInputScripts()
        {
            // GetComponents<MonoBehaviour>() (générique) est strippé en IL2CPP
            // MiSide. On utilise l'overload non-générique avec Il2CppType.Of.
            var comps = this.GetComponentsSafe<MonoBehaviour>();
            foreach (var comp in comps)
            {
                if (comp == null || comp == this) continue;
                var typeName = comp.GetType().FullName ?? string.Empty;
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
            // v1.3.6 — En v1.3.5 on utilisait AddComponent(typeof(TextMesh))
            // qui prend un System.Type → overload STRIPPÉ en IL2CPP MiSide
            // ("Method not found: AddComponent(System.Type)"). On passe par
            // notre helper AddComponentSafe<T> qui tente AddComponent(Il2CppSystem.Type)
            // via réflexion puis fallback générique. Si TextMesh est complètement
            // absent du build IL2CPP MiSide, le helper retourne null et on
            // annule proprement (le nametag est cosmétique).
            _nameTag = new GameObject("P1_NameTag");
            _nameTag.transform.SetParent(transform);
            _nameTag.transform.localPosition = Vector3.up * 2.6f;

            var tm = _nameTag.AddComponentSafe<TextMesh>();
            if (tm == null)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    "[Co-op] TextMesh AddComponent returned non-TextMesh proxy; P1 nametag skipped.");
                UnityEngine.Object.Destroy(_nameTag);
                _nameTag = null;
                return;
            }
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
            // GetComponentInChildren<Camera>() (générique) est strippé en
            // IL2CPP MiSide. On utilise l'helper safe (overload non-générique
            // + TryCast).
            var cam = this.GetComponentInChildrenSafe<Camera>();
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

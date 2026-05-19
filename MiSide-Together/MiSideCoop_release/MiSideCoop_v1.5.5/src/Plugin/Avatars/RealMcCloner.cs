using System;
using UnityEngine;

namespace MiSideCoop.Avatars
{
    /// <summary>
    /// v1.5.0 — Clone le GameObject du MC local de MiSide pour produire un
    /// "fantôme" 3D réaliste qu'on utilise comme représentation visuelle du
    /// peer, à la place du capsule + sphère synthétique des v1.3.x — v1.4.x.
    ///
    /// v1.5.3 — Clone 'Person' (sous-GO 3rd-person body) plutôt que 'Player'
    /// entier, pour éviter le double Animator (Player Arms first-person vide
    /// vs Person 3rd-person avec controller MiSide).
    ///
    /// v1.5.4 — Prépare l'Animator du ghost pour qu'il joue correctement :
    ///   • cullingMode = AlwaysAnimate (évite le gel du ghost hors-champ)
    ///   • applyRootMotion = false (évite le fight contre notre Lerp réseau)
    ///   • enabled = true (défensif)
    /// </summary>
    internal static class RealMcCloner
    {
        /// <summary>
        /// Tente de cloner le MC local. Retourne null en cas d'échec — le
        /// caller doit alors fallback sur CreateDefaultHumanoid.
        /// </summary>
        public static GameObject CloneLocalMc(string goName)
        {
            try
            {
                var mc = SceneDiagnostics.FindMcHeuristic();
                if (mc == null)
                {
                    MiSideCoopPlugin.Logger?.LogInfo(
                        "[Co-op] RealMcCloner: local MC not found yet (probably still in menu). "
                      + "Fall back to synthetic humanoid.");
                    return null;
                }

                // v1.5.3 — Clone le sous-GO "Person" si présent (3rd-person body
                // avec controller MiSide). Sinon clone Player entier.
                GameObject cloneSource = mc;
                try
                {
                    var personT = mc.transform.Find("Person");
                    if (personT != null)
                    {
                        cloneSource = personT.gameObject;
                        MiSideCoopPlugin.Logger?.LogInfo(
                            "[Co-op] RealMcCloner: cloning 'Person' sub-GO (clean 3rd-person body).");
                    }
                    else
                    {
                        MiSideCoopPlugin.Logger?.LogInfo(
                            "[Co-op] RealMcCloner: 'Person' child not found, cloning full MC root.");
                    }
                }
                catch { }

                Vector3    srcPos = cloneSource.transform.position;
                Quaternion srcRot = cloneSource.transform.rotation;

                var clone = UnityEngine.Object.Instantiate(cloneSource);
                if (clone == null)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        "[Co-op] RealMcCloner: Instantiate returned null. Fall back to synthetic.");
                    return null;
                }
                clone.name = goName;
                clone.transform.SetParent(null, true);

                clone.transform.position = srcPos + new Vector3(1.2f, 0f, 0f);
                clone.transform.rotation = srcRot;

                // Strip défensif + re-activation + re-layer + prep animator.
                StripInterferingComponents(clone);
                PrepareGhostAnimator(clone);

                try { UnityEngine.Object.DontDestroyOnLoad(clone); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        $"[Co-op] RealMcCloner.DontDestroyOnLoad: {ex.Message}");
                }

                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] RealMcCloner: cloned '{cloneSource.name}' → '{goName}' (3D ghost).");
                return clone;
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogError(
                    $"[Co-op] RealMcCloner.CloneLocalMc failed: {ex.Message}");
                return null;
            }
        }

        // ── v1.5.4 — Préparation de l'Animator du ghost ──────────────────────
        //
        // Le clone hérite des réglages Animator du MC original :
        //   • cullingMode = CullUpdateTransforms (défaut MiSide)
        //   • applyRootMotion = true (souvent — pour le MC qui se déplace)
        // Ces deux réglages empêchent le ghost de jouer correctement les anims
        // côté hôte (le ghost reste figé OU se téléporte chaotiquement).
        //
        // On force donc explicitement, sur tous les Animators du clone :
        //   • cullingMode = AlwaysAnimate → l'animator continue à jouer même
        //     si Unity pense que le SMR du clone est hors champ.
        //   • applyRootMotion = false → pas de fight contre PlayerAvatar.Lerp.
        //   • enabled = true → défensif.
        //
        // On loggue aussi l'état de l'Animator principal pour diagnostic.
        private static void PrepareGhostAnimator(GameObject root)
        {
            try
            {
                var animators = root.transform.GetComponentsInChildrenSafe<Animator>(true);
                int prepared = 0;
                foreach (var anim in animators)
                {
                    if (anim == null) continue;
                    try { anim.cullingMode    = AnimatorCullingMode.AlwaysAnimate; } catch { }
                    try { anim.applyRootMotion = false; }                            catch { }
                    try { anim.enabled        = true; }                              catch { }
                    prepared++;
                }

                // Détail du premier Animator (le principal a priori).
                if (animators.Length > 0)
                {
                    var main = animators[0];
                    try
                    {
                        string ctrl = main.runtimeAnimatorController != null
                            ? main.runtimeAnimatorController.name
                            : "<null>";
                        int layers = -1, pcount = -1;
                        try { layers = main.layerCount; }     catch { }
                        try { pcount = main.parameterCount; } catch { }
                        MiSideCoopPlugin.Logger?.LogInfo(
                            $"[Co-op] RealMcCloner.PrepareGhostAnimator: {prepared} animator(s) configured. "
                          + $"Main animator on '{main.gameObject.name}' — controller='{ctrl}', "
                          + $"layers={layers}, params={pcount}, cullingMode={main.cullingMode}, "
                          + $"applyRootMotion={main.applyRootMotion}, enabled={main.enabled}.");
                    }
                    catch (Exception ex)
                    {
                        MiSideCoopPlugin.Logger?.LogWarning(
                            $"[Co-op] RealMcCloner.PrepareGhostAnimator: detail log failed: {ex.Message}");
                    }
                }
                else
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        "[Co-op] RealMcCloner.PrepareGhostAnimator: NO Animator found on clone hierarchy. "
                      + "Animations will not play. Source MC may have its Animator at an unexpected path.");
                }
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] RealMcCloner.PrepareGhostAnimator: {ex.Message}");
            }
        }

        // ── Strip défensif ────────────────────────────────────────────────────
        // On supprime tout ce qui peut "vivre sa vie" sur le clone et entrer
        // en conflit avec le MC local. On garde l'Animator (utilisé par
        // AvatarAnimatorSync) et la hiérarchie de mesh.
        private static void StripInterferingComponents(GameObject root)
        {
            // v1.5.3 — RE-LAYER vers 0 (default) pour que TOUTES les cameras voient le clone.
            try
            {
                int relayered = 0;
                ForEachChildTransform(root.transform, child =>
                {
                    if (child == null || child.gameObject == null) return;
                    if (child.gameObject.layer != 0)
                    {
                        try { child.gameObject.layer = 0; relayered++; } catch { }
                    }
                });
                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] RealMcCloner: re-layered {relayered} GameObject(s) → layer 0 "
                  + "(default, visible by all cameras).");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] RealMcCloner.Relayer: {ex.Message}");
            }

            // v1.5.3 — Material audit pour éviter le pink fluo Unity.
            try
            {
                var smrs = root.transform.GetComponentsInChildrenSafe<SkinnedMeshRenderer>(true);
                Material referenceMat = null;
                int nullCount = 0, fixedCount = 0;

                foreach (var smr in smrs)
                {
                    if (smr == null) continue;
                    try
                    {
                        if (smr.sharedMaterial != null) { referenceMat = smr.sharedMaterial; break; }
                    }
                    catch { }
                }

                foreach (var smr in smrs)
                {
                    if (smr == null) continue;
                    try
                    {
                        if (smr.sharedMaterial == null)
                        {
                            nullCount++;
                            MiSideCoopPlugin.Logger?.LogWarning(
                                $"[Co-op]   SMR '{smr.gameObject.name}' has NULL sharedMaterial "
                              + "(would render pink). Applying fallback.");
                            if (referenceMat != null)
                            {
                                smr.sharedMaterial = referenceMat;
                                fixedCount++;
                            }
                        }
                    }
                    catch { }
                }
                if (nullCount > 0 || fixedCount > 0)
                {
                    MiSideCoopPlugin.Logger?.LogInfo(
                        $"[Co-op] RealMcCloner: material audit — {nullCount} SMR(s) had null material, "
                      + $"{fixedCount} patched with fallback (ref='{referenceMat?.name ?? "none"}').");
                }
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] RealMcCloner.MaterialAudit: {ex.Message}");
            }

            // v1.5.1 — Ré-activation des GameObjects désactivés (tête, cheveux…)
            try
            {
                int activated = 0;
                ForEachChildTransform(root.transform, child =>
                {
                    if (child == null || child.gameObject == null) return;
                    if (!child.gameObject.activeSelf)
                    {
                        try { child.gameObject.SetActive(true); activated++; } catch { }
                    }
                });
                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] RealMcCloner: re-activated {activated} hidden child GameObject(s) on clone.");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] RealMcCloner.ReactivateChildren: {ex.Message}");
            }

            // v1.4.10 — Ré-activation des renderers cachés.
            try
            {
                var meshRends = root.transform.GetComponentsInChildrenSafe<MeshRenderer>(true);
                foreach (var mr in meshRends)
                {
                    if (mr == null) continue;
                    try { mr.enabled = true; } catch { }
                }
                var skinned = root.transform.GetComponentsInChildrenSafe<SkinnedMeshRenderer>(true);
                foreach (var smr in skinned)
                {
                    if (smr == null) continue;
                    try { smr.enabled = true; } catch { }
                }

                // v1.5.4 — Force aussi le refresh des bounds des SMR.
                // Sans ça, les bounds peuvent être héritées "vides" du prefab
                // source, et Unity culle le SMR → Animator passé en CullUpdateTransforms.
                // updateWhenOffscreen = true coûte un peu de CPU mais garantit
                // que le ghost est toujours visible et animé.
                foreach (var smr in skinned)
                {
                    if (smr == null) continue;
                    try { smr.updateWhenOffscreen = true; } catch { }
                }

                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] RealMcCloner: enabled {meshRends.Length} MeshRenderer(s) and "
                  + $"{skinned.Length} SkinnedMeshRenderer(s) on clone (incl. head/hair/face, "
                  + "updateWhenOffscreen=true for anim culling fix).");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] RealMcCloner.EnableAllRenderers: {ex.Message}");
            }

            // 1) Caméras enfants : désactivées (Destroy peut crasher via FlareLayer).
            try
            {
                var cams = root.transform.GetComponentsInChildrenSafe<Camera>(true);
                foreach (var c in cams)
                {
                    if (c == null) continue;
                    try { c.enabled = false; } catch { }
                    try { c.tag = "Untagged"; } catch { } // libère MainCamera
                }
            }
            catch { }

            // 2) AudioListeners enfants : désactivés.
            try
            {
                var als = root.transform.GetComponentsInChildrenSafe<AudioListener>(true);
                foreach (var a in als)
                {
                    if (a == null) continue;
                    try { a.enabled = false; } catch { }
                }
            }
            catch { }

            // 3) CharacterController désactivé + effacé.
            try
            {
                var ccs = root.transform.GetComponentsInChildrenSafe<CharacterController>(true);
                foreach (var cc in ccs)
                {
                    if (cc == null) continue;
                    try { cc.enabled = false; } catch { }
                    try { UnityEngine.Object.Destroy(cc); } catch { }
                }
            }
            catch { }

            // 4) Rigidbody → kinematic.
            try
            {
                var rbs = root.transform.GetComponentsInChildrenSafe<Rigidbody>(true);
                foreach (var rb in rbs)
                {
                    if (rb == null) continue;
                    try { rb.isKinematic = true; rb.useGravity = false; } catch { }
                }
            }
            catch { }

            // 5) Colliders → trigger.
            try
            {
                var cols = root.transform.GetComponentsInChildrenSafe<Collider>(true);
                foreach (var col in cols)
                {
                    if (col == null) continue;
                    try { col.isTrigger = true; } catch { }
                }
            }
            catch { }

            // 6) MonoBehaviour custom MiSide → désactivés (sauf nos propres composants).
            try
            {
                var mbs = root.transform.GetComponentsInChildrenSafe<MonoBehaviour>(true);
                foreach (var mb in mbs)
                {
                    if (mb == null) continue;
                    var t = mb.GetType();
                    string ns = t.Namespace ?? string.Empty;
                    if (ns.StartsWith("MiSideCoop")) continue;
                    try { mb.enabled = false; } catch { }
                }
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] RealMcCloner.StripMonoBehaviours: {ex.Message}");
            }
        }

        /// <summary>
        /// Walk récursif de la hiérarchie transform. IL2CPP-safe.
        /// </summary>
        private static void ForEachChildTransform(Transform root, Action<Transform> action, int depth = 0)
        {
            if (root == null || depth > 16) return;
            try { action(root); } catch { }
            int n;
            try { n = root.childCount; }
            catch { return; }
            for (int i = 0; i < n; i++)
            {
                Transform c = null;
                try { c = root.GetChild(i); }
                catch { continue; }
                if (c != null) ForEachChildTransform(c, action, depth + 1);
            }
        }
    }
}

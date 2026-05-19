using System;
using UnityEngine;

namespace MiSideCoop.Avatars
{
    /// <summary>
    /// v1.5.0 — Clone le GameObject du MC local de MiSide pour produire un
    /// "fantôme" 3D réaliste qu'on utilise comme représentation visuelle du
    /// peer, à la place du capsule + sphère synthétique des v1.3.x — v1.4.x.
    ///
    /// Stratégie :
    ///   1) Trouve le MC local via SceneDiagnostics.FindMcHeuristic (renvoie
    ///      le GameObject 'Player' en MiSide 0.93L).
    ///   2) UnityEngine.Object.Instantiate(mc) → copie complète de la hiérarchie
    ///      (mesh, SkinnedMeshRenderer, Animator, bones, materials).
    ///   3) Détache du parent (au cas où le MC est enfant d'un manager).
    ///   4) Renomme proprement.
    ///   5) Marque DontDestroyOnLoad — le fantôme persiste entre les scènes
    ///      comme les avatars synthétiques v1.3.9+.
    ///   6) Strip défensif des composants qui interfèreraient avec le rôle de
    ///      pur "fantôme visuel" :
    ///        - tous les MonoBehaviour custom (PlayerMove, contrôles, audio
    ///          listeners, etc.) sauf l'Animator
    ///        - CharacterController / Rigidbody (deviennent isKinematic /
    ///          isTrigger pour ne pas bloquer la physique du MC local)
    ///        - Camera children (sinon conflit MainCamera)
    ///        - AudioListener children (sinon Unity log warning)
    ///
    /// Si l'Instantiate échoue (rare en IL2CPP), on retombe sur l'humanoïde
    /// synthétique capsule + sphère (v1.4.x).
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

                // v1.5.3 — STRATÉGIE DE CLONE OPTIMISÉE
                //
                // Le F10 dump (v1.5.2) a révélé la structure MiSide réelle :
                //   Player (root)
                //   ├── HeadPlayer/Player Arms [Anim, controller vide]  ← first-person only
                //   ├── Person [Anim, controller MiSide]                ← VRAI body 3rd-person
                //   │   ├── Armature/Hips/Spine/Chest/Neck/Head + Eyes
                //   │   ├── Arms [SMR:on] layer=7
                //   │   ├── Clothes [SMR:on] layer=7
                //   │   ├── HeadMirror [SMR:on] layer=14 ← MIRROR layer culled normalement
                //   │   └── HairMirror [SMR:on] layer=14 ← idem
                //   └── (Tetris game, FixPositions, Hand Door, etc.)
                //
                // Donc le BON candidat à cloner = "Person" (pas "Player").
                // Avantages :
                //   • Pas de double Animator ('Player Arms' vide vs 'Person' avec controller)
                //   • Pas de Tetris game, FixPositions, etc. qui n'ont rien à faire sur le ghost
                //   • Hiérarchie 5× plus légère
                //   • HeadMirror/HairMirror inclus → tête visible après re-layer
                //
                // Si "Person" introuvable (autre version MiSide), fallback sur Player entier.
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

                // Capture la position du MC AVANT clone.
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

                // Strip défensif + re-activation + re-layer.
                StripInterferingComponents(clone);

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

        // ── Strip défensif ────────────────────────────────────────────────────
        // On supprime tout ce qui peut "vivre sa vie" sur le clone et entrer
        // en conflit avec le MC local. On garde l'Animator (utilisé par
        // AvatarAnimatorSync) et la hiérarchie de mesh.
        private static void StripInterferingComponents(GameObject root)
        {
            // v1.5.3 — RE-LAYER vers 0 (default) pour que TOUTES les cameras voient le clone.
            //
            // Diagnostic du F10 dump (v1.5.2) :
            //   • HeadMirror layer=14, HairMirror layer=14 → "MirrorLayer", culled
            //     par la camera principale MiSide → tête invisible.
            //   • Arms/Clothes layer=7 → "Player" layer → visible.
            //
            // En forçant tous les renderers + leurs GameObjects parents en
            // layer 0 (default), on garantit que toutes les cameras les voient,
            // peu importe leur cullingMask.
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

            // v1.5.3 — DIAGNOSTIC + FIX TEMPORAIRE des materials manquants.
            //
            // Symptôme observé v1.5.2 (screenshot) : le mesh "Arms" rend en
            // pink fluo Unity = sharedMaterial == null. Cause probable :
            // MiSide set le material via MaterialPropertyBlock à runtime via
            // un script PlayerMove ou similaire qu'on désactive en bloc.
            //
            // Fix temporaire : on logue quels SMRs ont des materials null et
            // on tente de copier la material du premier SMR valide trouvé
            // dans la hiérarchie. C'est laid mais visuellement mieux qu'un
            // pink fluo.
            try
            {
                var smrs = root.transform.GetComponentsInChildrenSafe<SkinnedMeshRenderer>(true);
                Material referenceMat = null;
                int nullCount = 0, fixedCount = 0;

                // Pass 1 : trouve une material valide à utiliser comme référence.
                foreach (var smr in smrs)
                {
                    if (smr == null) continue;
                    try
                    {
                        if (smr.sharedMaterial != null) { referenceMat = smr.sharedMaterial; break; }
                    }
                    catch { }
                }

                // Pass 2 : log + fix.
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

            // v1.5.1 — RÉ-ACTIVATION DES GAMEOBJECTS DÉSACTIVÉS
            //
            // Le fix v1.4.10 (renderer.enabled = true) ne suffit pas si le
            // GameObject parent est SetActive(false). MiSide cache souvent les
            // meshes de tête/cheveux/visage via SetActive(false) sur le
            // GameObject lui-même (pas juste le renderer) — par ex :
            //   Player/Body/Head_GO (SetActive=false) → enfants invisibles
            //   même si SkinnedMeshRenderer.enabled = true.
            //
            // On force SetActive(true) sur tous les descendants. Risque : on
            // peut activer des GameObjects que MiSide voulait garder inactifs
            // pour des raisons gameplay (ex: items à révéler plus tard). Mais
            // c'est UN CLONE, donc ça n'affecte pas le gameplay du MC original.
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

            // v1.4.10 — RÉ-ACTIVATION DES RENDERERS CACHÉS (tête, cheveux, visage)
            //
            // MiSide est un jeu first-person : le MC a sa tête, ses cheveux et
            // son visage en MeshRenderer/SkinnedMeshRenderer dont .enabled=false
            // (sinon le joueur verrait l'intérieur de son crâne en regardant en
            // bas). Quand on clone, on copie cette config désactivée → le ghost
            // 3D apparaît sans tête (juste un corps acéphale).
            //
            // Solution : on force TOUS les renderers du clone à .enabled=true.
            // Pour les autres meshes (corps, vêtements), c'était déjà true donc
            // c'est un no-op. Pour la tête / cheveux / visage : ça les rend
            // visibles côté distant. ✅
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
                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] RealMcCloner: enabled {meshRends.Length} MeshRenderer(s) and "
                  + $"{skinned.Length} SkinnedMeshRenderer(s) on clone (incl. head/hair/face).");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] RealMcCloner.EnableAllRenderers: {ex.Message}");
            }

            // 1) Caméras enfants : DÉSACTIVÉES au lieu de Destroyed.
            //    v1.4.6 — Destroy(Camera) échoue avec
            //    "Can't remove Camera because FlareLayer depends on it" si
            //    le MC a un FlareLayer en composant frère. Idem AudioListener
            //    avec AudioChorusFilter. La désactivation suffit à éviter
            //    le conflit MainCamera.
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

            // 2) AudioListeners enfants : DÉSACTIVÉS (AudioChorusFilter dépend).
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

            // 3) CharacterController → désactivé (Destroy peut crasher si
            //    référencé par un autre script du clone qu'on n'a pas encore
            //    nettoyé). On le désactive + on l'efface.
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

            // 4) Rigidbody → kinematic (pour ne pas tomber via la gravité) +
            //    on retire la possibilité de pousser le MC local.
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

            // 5) Colliders → tous en trigger pour qu'ils ne bloquent jamais
            //    le MC local. On ne les détruit PAS car certains scripts MiSide
            //    récupèrent le Collider par GetComponent et pourraient NRE.
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

            // 6) MonoBehaviour custom MiSide (PlayerMove, etc.) → on les
            //    désactive en bloc. Stratégie : on récupère tous les
            //    MonoBehaviour, on garde seulement l'Animator (qui n'est PAS
            //    un MonoBehaviour mais une Behaviour, donc pas matché) et nos
            //    propres composants MiSideCoop.*. Tout le reste : .enabled = false.
            //
            //    Le Destroy est trop dangereux ici car beaucoup de scripts MiSide
            //    s'auto-référencent. .enabled = false suffit à les neutraliser.
            try
            {
                var mbs = root.transform.GetComponentsInChildrenSafe<MonoBehaviour>(true);
                foreach (var mb in mbs)
                {
                    if (mb == null) continue;
                    var t = mb.GetType();
                    string ns = t.Namespace ?? string.Empty;
                    // Préserver nos propres composants
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
        /// v1.5.1 — Walk récursif de la hiérarchie transform.
        /// IL2CPP-safe (utilise GetChild + childCount uniquement).
        /// Max depth 16 pour éviter les boucles infinies si cycle (jamais en
        /// pratique en Unity, garde-fou défensif).
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

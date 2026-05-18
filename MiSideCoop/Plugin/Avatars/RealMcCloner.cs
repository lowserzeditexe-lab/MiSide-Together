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

                // Capture la position du MC AVANT clone (pour positionner le
                // clone à un offset visible plutôt qu'exactement sur le MC).
                Vector3    srcPos = mc.transform.position;
                Quaternion srcRot = mc.transform.rotation;

                var clone = UnityEngine.Object.Instantiate(mc);
                if (clone == null)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        "[Co-op] RealMcCloner: Instantiate returned null. Fall back to synthetic.");
                    return null;
                }
                clone.name = goName;
                clone.transform.SetParent(null, true);

                // Position initiale légèrement décalée pour ne pas chevaucher
                // exactement le MC local (le stream réseau va ensuite update
                // la position).
                clone.transform.position = srcPos + new Vector3(1.2f, 0f, 0f);
                clone.transform.rotation = srcRot;

                // Strip défensif — IL2CPP-safe via try/catch granulaires.
                StripInterferingComponents(clone);

                try { UnityEngine.Object.DontDestroyOnLoad(clone); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        $"[Co-op] RealMcCloner.DontDestroyOnLoad: {ex.Message}");
                }

                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] RealMcCloner: cloned MC '{mc.name}' → '{goName}' (3D model ghost).");
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
            // 1) Caméras enfants (sinon 2× MainCamera → ambigu).
            try
            {
                var cams = root.GetComponentsInChildrenSafe<Camera>(true);
                foreach (var c in cams)
                {
                    if (c == null) continue;
                    try { UnityEngine.Object.Destroy(c); } catch { }
                }
            }
            catch { }

            // 2) AudioListeners enfants (warning Unity sinon).
            try
            {
                var als = root.GetComponentsInChildrenSafe<AudioListener>(true);
                foreach (var a in als)
                {
                    if (a == null) continue;
                    try { UnityEngine.Object.Destroy(a); } catch { }
                }
            }
            catch { }

            // 3) CharacterController → désactivé (Destroy peut crasher si
            //    référencé par un autre script du clone qu'on n'a pas encore
            //    nettoyé). On le désactive + on l'efface.
            try
            {
                var ccs = root.GetComponentsInChildrenSafe<CharacterController>(true);
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
                var rbs = root.GetComponentsInChildrenSafe<Rigidbody>(true);
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
                var cols = root.GetComponentsInChildrenSafe<Collider>(true);
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
                var mbs = root.GetComponentsInChildrenSafe<MonoBehaviour>(true);
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
    }
}

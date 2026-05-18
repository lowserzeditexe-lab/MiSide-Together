using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MiSideCoop.Avatars
{
    /// <summary>
    /// Diagnostic IL2CPP-safe pour identifier des GameObjects intéressants
    /// (MC, caméras, NPC) à partir de la scène active.
    ///
    /// Utilisé par CoopBootstrap.Update sur appui F9 pour produire un dump
    /// exploitable du contenu de la scène — permet de pré-câbler les noms
    /// exacts du MC, des spawn points et des entités Mita pour les versions
    /// futures sans dépendre d'un dump Cpp2IL externe.
    ///
    /// Toutes les API utilisées ici sont IL2CPP-safe en MiSide :
    ///   • GameObject.Find(string)
    ///   • Camera.main
    ///   • Transform.childCount / GetChild(int)
    ///   • Component.GetComponent&lt;T&gt;()  (générique singulier)
    /// </summary>
    internal static class SceneDiagnostics
    {
        /// <summary>
        /// Cherche le GameObject correspondant au MC dans la scène active via
        /// une heuristique multi-critères, IL2CPP-safe.
        ///
        /// Stratégie :
        ///   1) Tentative par nom exact (liste élargie 0.93L-aware).
        ///   2) Heuristique : descendant de Camera.main.transform.root qui porte
        ///      un Rigidbody OU un CharacterController + un Animator (en lui
        ///      ou en descendant), et dont le nom ne ressemble pas à Mita.
        ///   3) Fallback : Camera.main.transform.root nu, comme avant.
        /// </summary>
        public static GameObject FindMcHeuristic()
        {
            // 1) Noms candidats — version étendue pour MiSide 0.93L.
            //    On garde "male_mc" historique au top puis on élargit.
            string[] names =
            {
                "male_mc", "Male_MC", "MaleMC",
                "MC", "Mc", "mc",
                "PlayerCharacter", "MainCharacter", "Main_Character",
                "PlayerController", "Player_Character",
                "Hero", "Protagonist",
                "Mita_MC", "Player_MC", "MC_Player",
                "Character", "Char_Player",
            };
            foreach (var n in names)
            {
                var go = SafeFind(n);
                if (go != null) return go;
            }

            // 2) Heuristique via Camera.main.transform.root.
            //    On scanne RÉCURSIVEMENT le sous-arbre, on prend le premier
            //    nœud qui a (Rigidbody OU CharacterController) + Animator
            //    quelque part, et dont le nom ne contient pas de mot
            //    "mita/menu/canvas/ui".
            try
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    var root = cam.transform.root;
                    if (root != null)
                    {
                        var match = TraverseForMcCandidate(root);
                        if (match != null) return match.gameObject;
                    }
                }
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] FindMcHeuristic camera traversal failed: {ex.Message}");
            }

            // 3) Fallback : root de la caméra principale (comportement v1.3.6).
            try
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    var root = cam.transform.root.gameObject;
                    if (root != null && !LooksLikeMenu(root.name)) return root;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Désactive toutes les <see cref="Camera"/> visibles dans la scène
        /// à partir de Camera.main.transform.root. Remplacement de
        /// <c>Scene.GetRootGameObjects()</c> (strippé en IL2CPP MiSide 0.93L).
        /// </summary>
        public static void DisableExistingCamerasSafe()
        {
            try
            {
                var main = Camera.main;
                if (main == null) return;
                var root = main.transform.root;
                if (root == null) return;
                var cams = root.GetComponentsInChildrenSafe<Camera>(true);
                foreach (var c in cams)
                {
                    if (c == null) continue;
                    try { c.enabled = false; }
                    catch { /* défensif */ }
                }
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] DisableExistingCamerasSafe: {ex.Message}");
            }
        }

        /// <summary>
        /// Dump exhaustif F9 : liste tous les candidats MC, caméras et
        /// Animators visibles à partir de Camera.main.transform.root.
        /// Utilisé pour identifier les noms réels en 0.93L sans dump Cpp2IL.
        /// </summary>
        public static void DumpScene()
        {
            var log = MiSideCoopPlugin.Logger;
            if (log == null) return;

            try
            {
                log.LogMessage("════════════════════════════════════════");
                log.LogMessage("[Co-op DIAG] Scene dump start (F9)");
                log.LogMessage("════════════════════════════════════════");

                var cam = Camera.main;
                if (cam == null)
                {
                    log.LogMessage("[Co-op DIAG] Camera.main == null — no anchor for scan.");
                    return;
                }
                var root = cam.transform.root;
                log.LogMessage($"[Co-op DIAG] Camera.main.transform.root = '{root.name}' (path='{PathOf(root)}')");

                var animators = root.GetComponentsInChildrenSafe<Animator>(true);
                log.LogMessage($"[Co-op DIAG] Animators under root: {animators.Length}");
                int max = Math.Min(animators.Length, 32);
                for (int i = 0; i < max; i++)
                {
                    var a = animators[i];
                    if (a == null) continue;
                    var ago = a.gameObject;
                    log.LogMessage(
                        $"[Co-op DIAG]   anim[{i}] go='{ago.name}'  path='{PathOf(ago.transform)}'  " +
                        $"active={ago.activeInHierarchy}  rb={Has<Rigidbody>(ago)}  cc={Has<CharacterController>(ago)}");
                }

                var cams = root.GetComponentsInChildrenSafe<Camera>(true);
                log.LogMessage($"[Co-op DIAG] Cameras under root: {cams.Length}");
                for (int i = 0; i < Math.Min(cams.Length, 16); i++)
                {
                    var c = cams[i];
                    if (c == null) continue;
                    log.LogMessage(
                        $"[Co-op DIAG]   cam[{i}] go='{c.gameObject.name}'  tag='{c.tag}'  " +
                        $"enabled={c.enabled}  path='{PathOf(c.transform)}'");
                }

                var rbs = root.GetComponentsInChildrenSafe<Rigidbody>(true);
                log.LogMessage($"[Co-op DIAG] Rigidbodies under root: {rbs.Length}");
                for (int i = 0; i < Math.Min(rbs.Length, 16); i++)
                {
                    var r = rbs[i];
                    if (r == null) continue;
                    log.LogMessage(
                        $"[Co-op DIAG]   rb[{i}] go='{r.gameObject.name}'  kinematic={r.isKinematic}  " +
                        $"path='{PathOf(r.transform)}'");
                }

                var ccs = root.GetComponentsInChildrenSafe<CharacterController>(true);
                log.LogMessage($"[Co-op DIAG] CharacterControllers under root: {ccs.Length}");
                for (int i = 0; i < Math.Min(ccs.Length, 16); i++)
                {
                    var cc = ccs[i];
                    if (cc == null) continue;
                    log.LogMessage(
                        $"[Co-op DIAG]   cc[{i}] go='{cc.gameObject.name}'  path='{PathOf(cc.transform)}'");
                }

                // MC heuristique
                var mc = FindMcHeuristic();
                log.LogMessage(
                    mc != null
                        ? $"[Co-op DIAG] HEURISTIC MC PICK = '{mc.name}'  path='{PathOf(mc.transform)}'"
                        : "[Co-op DIAG] HEURISTIC MC PICK = (none)");

                log.LogMessage("════════════════════════════════════════");
                log.LogMessage("[Co-op DIAG] Scene dump end");
                log.LogMessage("════════════════════════════════════════");
            }
            catch (Exception ex)
            {
                log.LogError($"[Co-op DIAG] DumpScene failed: {ex.Message}");
            }
        }

        // ── Helpers internes ──────────────────────────────────────────────────

        private static Transform TraverseForMcCandidate(Transform tr)
        {
            if (tr == null) return null;
            var go = tr.gameObject;
            if (go != null && go.activeInHierarchy && !LooksLikeMenu(go.name) && !LooksLikeMita(go.name))
            {
                bool hasRb = Has<Rigidbody>(go) || Has<CharacterController>(go);
                if (hasRb)
                {
                    var anim = tr.GetComponentInChildrenSafe<Animator>(false);
                    if (anim != null) return tr;
                }
            }
            int n;
            try { n = tr.childCount; } catch { return null; }
            for (int i = 0; i < n; i++)
            {
                Transform child = null;
                try { child = tr.GetChild(i); } catch { continue; }
                if (child == null) continue;
                var found = TraverseForMcCandidate(child);
                if (found != null) return found;
            }
            return null;
        }

        private static bool Has<T>(GameObject go) where T : Component
        {
            if (go == null) return false;
            try { return go.GetComponent<T>() != null; }
            catch { return false; }
        }

        private static GameObject SafeFind(string n)
        {
            try { return GameObject.Find(n); }
            catch { return null; }
        }

        private static bool LooksLikeMenu(string n)
        {
            if (string.IsNullOrEmpty(n)) return true;
            var lo = n.ToLowerInvariant();
            return lo.Contains("menu") || lo.Contains("canvas") || lo.Contains("ui_") ||
                   lo == "ui"          || lo.Contains("splash") || lo.Contains("title");
        }

        private static bool LooksLikeMita(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            var lo = n.ToLowerInvariant();
            return lo.Contains("mita") || lo.Contains("person") || lo.Contains("npc");
        }

        private static string PathOf(Transform tr)
        {
            if (tr == null) return "(null)";
            var sb = new StringBuilder(64);
            sb.Append(tr.name);
            var p = tr.parent;
            int safety = 16;
            while (p != null && safety-- > 0)
            {
                sb.Insert(0, p.name + "/");
                p = p.parent;
            }
            return sb.ToString();
        }
    }
}

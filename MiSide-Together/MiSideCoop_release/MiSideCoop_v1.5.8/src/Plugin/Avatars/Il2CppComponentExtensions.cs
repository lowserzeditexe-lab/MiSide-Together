using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiSideCoop.Avatars
{
    /// <summary>
    /// Helpers IL2CPP-safe pour les recherches de composants — version v1.3.4.
    ///
    /// HISTORIQUE
    /// ──────────
    /// v1.3.3 — Tentative #1 : on appelait l'overload non-générique
    /// <c>Component.GetComponentsInChildren(System.Type, bool)</c>. ÉCHEC :
    /// cet overload est ÉGALEMENT strippé dans le build IL2CPP de MiSide
    /// (logs : "Method not found: 'UnityEngine.Component[] UnityEngine.
    /// Component.GetComponentsInChildren(System.Type, Boolean)'").
    ///
    /// v1.3.4 — Tentative #2 (Plan B) : on n'utilise PLUS aucune méthode
    /// "GetComponents*" sur Component. À la place, on fait un TRAVERSAL
    /// RÉCURSIF MANUEL de la hiérarchie via :
    ///   • <c>Transform.childCount</c> (propriété de base, non strippée)
    ///   • <c>Transform.GetChild(int)</c> (accessor de base, non stripé)
    ///   • <c>Component.GetComponent&lt;T&gt;()</c> générique SINGULIER —
    ///     prouvé fonctionnel en IL2CPP MiSide (utilisé partout dans le
    ///     codebase v1.0+ : <c>mcGo.GetComponent&lt;Player1Avatar&gt;()</c>,
    ///     <c>body.GetComponent&lt;CapsuleCollider&gt;()</c>, etc.).
    ///     Il2CppInterop génère un wrapper dédié pour ce cas générique.
    ///
    /// LIMITATIONS
    /// ───────────
    /// • <c>GetComponentsSafe&lt;T&gt;()</c> ne récupère qu'UN composant par
    ///   GameObject (le premier que <c>GetComponent&lt;T&gt;()</c> retourne).
    ///   Acceptable pour notre usage : on cherche typiquement Renderer,
    ///   Animator, Camera, MonoBehaviour spécifiques — un seul par objet.
    /// • Performance : O(N) sur le nombre de Transforms du sous-arbre. Pour
    ///   un avatar Unity standard (~50-200 transforms) c'est négligeable.
    /// </summary>
    internal static class Il2CppComponentExtensions
    {
        // ── GetComponentsInChildren&lt;T&gt;(bool) ─────────────────────────────
        public static T[] GetComponentsInChildrenSafe<T>(this Component self, bool includeInactive = false)
            where T : Component
        {
            if (self == null) return Array.Empty<T>();
            var result = new List<T>(8);
            try
            {
                var root = self.transform;
                if (root != null) TraverseCollect<T>(root, includeInactive, result);
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] GetComponentsInChildrenSafe<{typeof(T).Name}> traversal failed: {ex.Message}");
            }
            return result.ToArray();
        }

        // ── GetComponentInChildren&lt;T&gt;(bool) ──────────────────────────────
        public static T GetComponentInChildrenSafe<T>(this Component self, bool includeInactive = false)
            where T : Component
        {
            if (self == null) return null;
            try
            {
                var root = self.transform;
                if (root == null) return null;
                return TraverseFindFirst<T>(root, includeInactive);
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] GetComponentInChildrenSafe<{typeof(T).Name}> traversal failed: {ex.Message}");
                return null;
            }
        }

        // ── GetComponents&lt;T&gt;() ───────────────────────────────────────────
        // Best-effort : retourne au plus UN composant (le 1er) car aucune
        // API multi-composant non-strippée n'est disponible en IL2CPP MiSide.
        // Le seul consommateur (Player1Avatar.DisableLocalInputScripts) gère
        // déjà le cas où on ne peut désactiver qu'un script à la fois.
        public static T[] GetComponentsSafe<T>(this Component self) where T : Component
        {
            if (self == null) return Array.Empty<T>();
            try
            {
                var c = self.GetComponent<T>();
                return c != null ? new T[] { c } : Array.Empty<T>();
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] GetComponentsSafe<{typeof(T).Name}> failed: {ex.Message}");
                return Array.Empty<T>();
            }
        }

        // ── Traversal interne ─────────────────────────────────────────────────
        private static void TraverseCollect<T>(Transform tr, bool includeInactive, List<T> acc)
            where T : Component
        {
            if (tr == null) return;
            var go = tr.gameObject;
            if (go == null) return;
            if (!includeInactive && !go.activeInHierarchy) return;

            // GetComponent<T>() générique singulier : Il2CppInterop génère un
            // wrapper IL2CPP-safe pour ce cas (token de type passé via fast path).
            T comp = null;
            try { comp = tr.GetComponent<T>(); }
            catch { /* défensif — un composant manquant n'interrompt pas la collecte */ }
            if (comp != null) acc.Add(comp);

            int n = 0;
            try { n = tr.childCount; } catch { return; }
            for (int i = 0; i < n; i++)
            {
                Transform child = null;
                try { child = tr.GetChild(i); } catch { continue; }
                if (child != null) TraverseCollect<T>(child, includeInactive, acc);
            }
        }

        private static T TraverseFindFirst<T>(Transform tr, bool includeInactive)
            where T : Component
        {
            if (tr == null) return null;
            var go = tr.gameObject;
            if (go == null) return null;
            if (!includeInactive && !go.activeInHierarchy) return null;

            T comp = null;
            try { comp = tr.GetComponent<T>(); }
            catch { }
            if (comp != null) return comp;

            int n = 0;
            try { n = tr.childCount; } catch { return null; }
            for (int i = 0; i < n; i++)
            {
                Transform child = null;
                try { child = tr.GetChild(i); } catch { continue; }
                if (child == null) continue;
                var found = TraverseFindFirst<T>(child, includeInactive);
                if (found != null) return found;
            }
            return null;
        }
    }
}

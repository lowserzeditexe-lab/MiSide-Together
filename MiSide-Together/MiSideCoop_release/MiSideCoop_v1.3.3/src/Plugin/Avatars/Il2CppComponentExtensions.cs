using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiSideCoop.Avatars
{
    /// <summary>
    /// Helpers IL2CPP-safe pour les recherches de composants.
    ///
    /// PROBLÈME — Dans le build IL2CPP de MiSide, les méthodes GÉNÉRIQUES de
    /// recherche de composants (<c>GetComponentsInChildren&lt;T&gt;()</c>,
    /// <c>GetComponentInChildren&lt;T&gt;()</c>, <c>GetComponents&lt;T&gt;()</c>)
    /// ont été strippées par Unity au stripping AOT. À l'appel runtime, le
    /// trampoline IL2CPP-to-Managed lève
    /// <c>MissingMethodException: "Method not found:
    /// '!!0[] UnityEngine.Component.GetComponentsInChildren()'"</c>.
    ///
    /// Cette exception remontait hors du callback réseau et empêchait
    /// l'initialisation des avatars (Player1/Player2) → le guest restait
    /// bloqué sur l'écran "CHARGEMENT..." après Join Room.
    ///
    /// SOLUTION — Utiliser les overloads NON-génériques de Component qui
    /// prennent un <see cref="System.Type"/> et qui ne sont PAS strippés.
    /// La couche Il2CppInterop convertit transparentement System.Type →
    /// Il2CppSystem.Type au runtime. On caste ensuite chaque résultat vers
    /// le type managé attendu via <c>TryCast&lt;T&gt;()</c>.
    /// </summary>
    internal static class Il2CppComponentExtensions
    {
        /// <summary>
        /// Équivalent IL2CPP-safe de <c>component.GetComponentsInChildren&lt;T&gt;(includeInactive)</c>.
        /// </summary>
        public static T[] GetComponentsInChildrenSafe<T>(this Component self, bool includeInactive = false)
            where T : Component
        {
            if (self == null) return Array.Empty<T>();
            try
            {
                var arr = self.GetComponentsInChildren(typeof(T), includeInactive);
                if (arr == null || arr.Length == 0) return Array.Empty<T>();

                var list = new List<T>(arr.Length);
                foreach (var c in arr)
                {
                    if (c == null) continue;
                    var casted = c as T;
                    if (casted != null) list.Add(casted);
                }
                return list.ToArray();
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] GetComponentsInChildrenSafe<{typeof(T).Name}> failed: {ex.Message}");
                return Array.Empty<T>();
            }
        }

        /// <summary>
        /// Équivalent IL2CPP-safe de <c>component.GetComponentInChildren&lt;T&gt;()</c>.
        /// </summary>
        public static T GetComponentInChildrenSafe<T>(this Component self, bool includeInactive = false)
            where T : Component
        {
            if (self == null) return null;
            try
            {
                var arr = self.GetComponentsInChildren(typeof(T), includeInactive);
                if (arr == null) return null;
                foreach (var c in arr)
                {
                    if (c == null) continue;
                    var casted = c as T;
                    if (casted != null) return casted;
                }
                return null;
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] GetComponentInChildrenSafe<{typeof(T).Name}> failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Équivalent IL2CPP-safe de <c>component.GetComponents&lt;T&gt;()</c>.
        /// </summary>
        public static T[] GetComponentsSafe<T>(this Component self) where T : Component
        {
            if (self == null) return Array.Empty<T>();
            try
            {
                var arr = self.GetComponents(typeof(T));
                if (arr == null || arr.Length == 0) return Array.Empty<T>();

                var list = new List<T>(arr.Length);
                foreach (var c in arr)
                {
                    if (c == null) continue;
                    var casted = c as T;
                    if (casted != null) list.Add(casted);
                }
                return list.ToArray();
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] GetComponentsSafe<{typeof(T).Name}> failed: {ex.Message}");
                return Array.Empty<T>();
            }
        }
    }
}

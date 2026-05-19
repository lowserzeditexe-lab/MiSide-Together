using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace MiSideCoop.Avatars
{
    /// <summary>
    /// Helper IL2CPP-safe pour <c>GameObject.AddComponent</c> — v1.3.6.
    ///
    /// PIÈGE RÉCURRENT
    /// ───────────────
    /// En IL2CPP MiSide, plusieurs overloads de <c>AddComponent</c> sont
    /// problématiques :
    ///
    ///   • <c>AddComponent&lt;T&gt;()</c> (générique) — Le dispatcher
    ///     <c>MethodInfoStoreGeneric_AddComponent_Public_T_0`1</c> lève
    ///     <c>TypeInitializationException</c> pour les types T dont la classe
    ///     IL2CPP n'existe pas (ex : <c>TextMesh</c>, absent du build MiSide
    ///     qui utilise TextMeshPro). Pour les types T enregistrés
    ///     (custom MonoBehaviour du mod), le générique fonctionne.
    ///
    ///   • <c>AddComponent(System.Type)</c> — STRIPPÉ. Le wrapped
    ///     <c>GameObject</c> exposé par Il2CppInterop n'a PAS d'overload
    ///     prenant <c>System.Type</c> ; seulement <c>Il2CppSystem.Type</c>.
    ///     Erreur runtime : "Method not found: AddComponent(System.Type)".
    ///
    ///   • <c>AddComponent(Il2CppSystem.Type)</c> — Existe au RUNTIME mais
    ///     <c>Il2CppSystem.Type</c> vit dans <c>Il2Cppmscorlib.dll</c>, un
    ///     assembly GÉNÉRÉ par Il2CppInterop.Generator depuis le binaire IL2CPP
    ///     du jeu et NON disponible au compile-time dans notre setup NuGet.
    ///     → On ne peut pas l'appeler directement en C# typé. On passe par
    ///     <see cref="Reflection"/>.
    ///
    /// STRATÉGIE
    /// ─────────
    /// <see cref="AddComponentSafe{T}"/> essaie dans l'ordre :
    ///   1) Réflexion : récupère <c>Il2CppType.Of&lt;T&gt;()</c> via
    ///      <see cref="Assembly.GetType"/> + <c>MakeGenericMethod</c>, puis
    ///      cherche l'overload <c>AddComponent</c> qui accepte le type retourné
    ///      (Il2CppSystem.Type) et l'invoque. Path IL2CPP-safe DÉFINITIVE.
    ///   2) Fallback : <c>AddComponent&lt;T&gt;()</c> générique (fonctionne
    ///      pour les types T enregistrés via le dispatcher Il2CppInterop).
    ///
    /// Le résultat est ensuite casté en T via le <c>as T</c> standard C#.
    /// </summary>
    internal static class Il2CppAddComponentHelper
    {
        // ── Caches réflexion ──────────────────────────────────────────────────
        private static bool       _reflectionResolved;
        private static MethodInfo _il2cppTypeOfGenericDef;   // Il2CppType.Of<T>() (generic def)
        private static MethodInfo _addComponentByIl2CppType; // GameObject.AddComponent(Il2CppSystem.Type)

        private static void ResolveReflectionOnce()
        {
            if (_reflectionResolved) return;
            _reflectionResolved = true;

            try
            {
                // 1) Trouve Il2CppInterop.Runtime.Il2CppType
                Type il2cppTypeClass = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "Il2CppInterop.Runtime")
                    {
                        il2cppTypeClass = asm.GetType("Il2CppInterop.Runtime.Il2CppType");
                        if (il2cppTypeClass != null) break;
                    }
                }
                if (il2cppTypeClass == null)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        "[Co-op] Il2CppType class not found via reflection; AddComponentSafe will fallback to generic.");
                    return;
                }

                // 2) Récupère la méthode statique générique Of<T>()
                _il2cppTypeOfGenericDef = il2cppTypeClass.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Of"
                                      && m.IsGenericMethodDefinition
                                      && m.GetParameters().Length == 0);
                if (_il2cppTypeOfGenericDef == null)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        "[Co-op] Il2CppType.Of<T>() not found; AddComponentSafe will fallback to generic.");
                    return;
                }

                // 3) Cherche l'overload GameObject.AddComponent(Il2CppSystem.Type).
                //    On itère TOUTES les méthodes du wrapped GameObject pour trouver
                //    celle qui s'appelle "AddComponent" et prend un seul paramètre
                //    de type Il2CppSystem.Type (déterminable seulement au runtime).
                var goRuntimeType = typeof(GameObject);
                // Détermine le type Il2CppSystem.Type via un Of<UnityEngine.Camera>() probe.
                object probe = null;
                try
                {
                    var probeMethod = _il2cppTypeOfGenericDef.MakeGenericMethod(typeof(UnityEngine.Camera));
                    probe = probeMethod.Invoke(null, null);
                }
                catch { }
                if (probe == null) return;

                var il2cppTypeRuntime = probe.GetType(); // = Il2CppSystem.Type
                _addComponentByIl2CppType = goRuntimeType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "AddComponent"
                                      && m.GetParameters().Length == 1
                                      && m.GetParameters()[0].ParameterType == il2cppTypeRuntime);

                if (_addComponentByIl2CppType == null)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        "[Co-op] GameObject.AddComponent(Il2CppSystem.Type) overload not found via reflection; using generic fallback.");
                }
                else
                {
                    MiSideCoopPlugin.Logger?.LogInfo(
                        "[Co-op] IL2CPP-safe AddComponent reflection wiring ready.");
                }
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] AddComponent reflection setup failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Ajoute un composant à un GameObject de manière IL2CPP-safe.
        /// Privilégie l'overload <c>AddComponent(Il2CppSystem.Type)</c> via
        /// réflexion ; tombe sur <c>AddComponent&lt;T&gt;()</c> générique en
        /// cas d'échec (qui marche pour les types enregistrés).
        /// </summary>
        public static T AddComponentSafe<T>(this GameObject go) where T : Component
        {
            if (go == null) return null;
            ResolveReflectionOnce();

            // Path 1 — Réflexion via Il2CppType.Of<T>() + AddComponent(Il2CppSystem.Type).
            if (_il2cppTypeOfGenericDef != null && _addComponentByIl2CppType != null)
            {
                try
                {
                    var ofConcrete = _il2cppTypeOfGenericDef.MakeGenericMethod(typeof(T));
                    var il2cppType = ofConcrete.Invoke(null, null);
                    if (il2cppType != null)
                    {
                        var result = _addComponentByIl2CppType.Invoke(go, new object[] { il2cppType });
                        var casted = result as T;
                        if (casted != null) return casted;
                        // Si on ne peut pas caster (proxy IL2CPP), on essaie GetComponent<T>
                        // qui devrait retrouver le composant fraîchement ajouté.
                        var fallback = go.GetComponent<T>();
                        if (fallback != null) return fallback;
                    }
                }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        $"[Co-op] AddComponentSafe<{typeof(T).Name}> reflection path failed: {ex.Message}");
                }
            }

            // Path 2 — Fallback générique. Pour les types T enregistrés par
            // Il2CppInterop (custom MonoBehaviours du mod), le dispatcher
            // générique fonctionne. Pour les types non enregistrés (ex: TextMesh
            // absent du build MiSide), il throw — le caller doit avoir un try/catch.
            try
            {
                return go.AddComponent<T>();
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogError(
                    $"[Co-op] AddComponentSafe<{typeof(T).Name}> generic fallback failed: {ex.Message}");
                return null;
            }
        }
    }
}

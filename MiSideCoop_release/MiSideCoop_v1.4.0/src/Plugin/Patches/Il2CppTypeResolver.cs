using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MiSideCoop.Patches
{
    /// <summary>
    /// Résolveur de type IL2CPP silencieux.
    ///
    /// PROBLÈME — <c>HarmonyLib.AccessTools.TypeByName(name)</c> appelle en interne
    /// <c>AccessTools.GetTypesFromAssembly(asm)</c> qui exécute <c>asm.GetTypes()</c>
    /// sur TOUTES les assemblies du AppDomain. Or, l'assembly de référence
    /// <c>UnityEngine.CoreModule</c> (NuGet) contient des stubs compiler-generated
    /// (<c>&lt;&gt;c</c>, <c>ControlOptions</c>, <c>CountOptions</c>) qui font lever
    /// un <c>ReflectionTypeLoadException</c> avec un message "format is invalid".
    /// HarmonyX logue ce warning à chaque appel → ~7 doublons polluent le log
    /// au démarrage.
    ///
    /// SOLUTION — On reproduit la sémantique de <c>TypeByName</c> mais on swallow
    /// <c>ReflectionTypeLoadException</c> en utilisant <c>ex.Types</c> (qui contient
    /// les types chargeables, le reste = null). Et on cache le résultat pour
    /// éviter de re-scanner à chaque appel.
    /// </summary>
    internal static class Il2CppTypeResolver
    {
        private static readonly Dictionary<string, Type> _cache = new(StringComparer.Ordinal);

        public static Type FindType(string fullOrShortName)
        {
            if (string.IsNullOrEmpty(fullOrShortName)) return null;
            if (_cache.TryGetValue(fullOrShortName, out var cached)) return cached;

            // 1) Type.GetType direct (rapide si fullname qualifié)
            var t = Type.GetType(fullOrShortName, throwOnError: false);
            if (t != null) return _cache[fullOrShortName] = t;

            // 2) Scan AppDomain — swallow les exceptions par assembly
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Récupère les types chargeables, ignore les corrompus
                    types = ex.Types.Where(x => x != null).ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var candidate in types)
                {
                    if (candidate == null) continue;
                    if (candidate.FullName == fullOrShortName ||
                        candidate.Name == fullOrShortName)
                    {
                        return _cache[fullOrShortName] = candidate;
                    }
                }
            }

            return _cache[fullOrShortName] = null;
        }
    }
}

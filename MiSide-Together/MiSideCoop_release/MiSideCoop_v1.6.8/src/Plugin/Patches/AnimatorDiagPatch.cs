using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MiSideCoop.Patches
{
    /// <summary>
    /// v1.5.9 - Diagnostic Harmony patches that intercept calls the MiSide
    /// game code makes on the local MC's "Person" Animator. We log the first
    /// N calls (per method type) to discover the real parameter names used
    /// to drive the body animation (walk / run / idle / etc.).
    ///
    /// Once we have the real names from the user's log, we can:
    ///   - Forward those SetFloat/SetBool calls into network messages
    ///   - Re-apply them on the remote ghost's "Person" clone
    ///
    /// All patches are NO-OP wrt the game (Prefix that returns void without
    /// modifying args, only logs). Self-disable after MaxLogPerMethod calls
    /// to avoid spamming the log forever.
    /// </summary>
    public static class AnimatorDiagPatch
    {
        // Maximum number of calls to log per method (then we go silent).
        private const int MaxLogPerMethod = 40;

        // Logged-method counters and seen-param tracking (process-wide).
        private static int _setFloatLogCount;
        private static int _setBoolLogCount;
        private static int _setTriggerLogCount;
        private static int _playLogCount;

        // Seen-param sets (so we log each distinct name at most once).
        private static readonly HashSet<string> _seenSetFloat   = new HashSet<string>();
        private static readonly HashSet<string> _seenSetBool    = new HashSet<string>();
        private static readonly HashSet<string> _seenSetTrigger = new HashSet<string>();
        private static readonly HashSet<int>    _seenPlayHash   = new HashSet<int>();

        // We only want to log calls targeting the MC's "Person" animator
        // (body) to filter out arms/UI/NPC animator noise. Optionally also
        // "Player Arms" for comparison.
        private static bool ShouldLog(Animator anim)
        {
            if (anim == null) return false;
            string name;
            try { name = anim.gameObject?.name ?? ""; }
            catch { return false; }
            // Match the local MC's body animator + arms (for comparison).
            // Also match the ghost clones so we can verify our pushes too.
            return name == "Person"
                || name == "Player Arms"
                || name == "Player2_Guest"
                || name == "Player1_Host_Remote";
        }

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetFloat), typeof(string), typeof(float))]
        private static class SetFloat_String_Patch
        {
            [HarmonyPostfix]
            private static void Postfix(Animator __instance, string name, float value)
            {
                try
                {
                    if (_setFloatLogCount >= MaxLogPerMethod) return;
                    if (!ShouldLog(__instance)) return;
                    var go = __instance.gameObject.name;
                    // Log once per (animator, param) pair when value is non-zero
                    // OR every 30th call so we can confirm even idle params.
                    var key = go + "/" + name;
                    if (_seenSetFloat.Add(key) || (value != 0f && _setFloatLogCount < 10))
                    {
                        _setFloatLogCount++;
                        MiSideCoopPlugin.Logger?.LogInfo(
                            $"[Co-op] anim-patch SetFloat: on='{go}' param='{name}' value={value:F3} (call #{_setFloatLogCount})");
                        if (_setFloatLogCount == MaxLogPerMethod)
                        {
                            MiSideCoopPlugin.Logger?.LogInfo(
                                "[Co-op] anim-patch SetFloat: reached log cap, going silent.");
                        }
                    }
                }
                catch { /* silent */ }
            }
        }

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetBool), typeof(string), typeof(bool))]
        private static class SetBool_String_Patch
        {
            [HarmonyPostfix]
            private static void Postfix(Animator __instance, string name, bool value)
            {
                try
                {
                    if (_setBoolLogCount >= MaxLogPerMethod) return;
                    if (!ShouldLog(__instance)) return;
                    var go = __instance.gameObject.name;
                    var key = go + "/" + name;
                    if (_seenSetBool.Add(key) || _setBoolLogCount < 15)
                    {
                        _setBoolLogCount++;
                        MiSideCoopPlugin.Logger?.LogInfo(
                            $"[Co-op] anim-patch SetBool: on='{go}' param='{name}' value={value} (call #{_setBoolLogCount})");
                        if (_setBoolLogCount == MaxLogPerMethod)
                        {
                            MiSideCoopPlugin.Logger?.LogInfo(
                                "[Co-op] anim-patch SetBool: reached log cap, going silent.");
                        }
                    }
                }
                catch { /* silent */ }
            }
        }

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetTrigger), typeof(string))]
        private static class SetTrigger_String_Patch
        {
            [HarmonyPostfix]
            private static void Postfix(Animator __instance, string name)
            {
                try
                {
                    if (_setTriggerLogCount >= MaxLogPerMethod) return;
                    if (!ShouldLog(__instance)) return;
                    var go = __instance.gameObject.name;
                    var key = go + "/" + name;
                    if (_seenSetTrigger.Add(key))
                    {
                        _setTriggerLogCount++;
                        MiSideCoopPlugin.Logger?.LogInfo(
                            $"[Co-op] anim-patch SetTrigger: on='{go}' param='{name}' (call #{_setTriggerLogCount})");
                    }
                }
                catch { /* silent */ }
            }
        }

        [HarmonyPatch(typeof(Animator), nameof(Animator.Play), typeof(int), typeof(int), typeof(float))]
        private static class Play_Int_Patch
        {
            [HarmonyPostfix]
            private static void Postfix(Animator __instance, int stateNameHash, int layer, float normalizedTime)
            {
                try
                {
                    if (_playLogCount >= MaxLogPerMethod) return;
                    if (!ShouldLog(__instance)) return;
                    var go = __instance.gameObject.name;
                    if (_seenPlayHash.Add(stateNameHash))
                    {
                        _playLogCount++;
                        MiSideCoopPlugin.Logger?.LogInfo(
                            $"[Co-op] anim-patch Play: on='{go}' stateHash={stateNameHash} layer={layer} normTime={normalizedTime:F3} (call #{_playLogCount})");
                    }
                }
                catch { /* silent */ }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using MiSideCoop.Avatars;

namespace MiSideCoop.UI
{
    /// <summary>
    /// v1.5.0 — Trouve et clique programmatiquement le bouton "Nouvelle Partie"
    /// du menu principal MiSide, de manière IL2CPP-safe.
    ///
    /// Utilisé par :
    ///   • Le bouton "DÉMARRER" du modal co-op host (clique localement)
    ///   • Le handler de GameLaunchMessage côté guest (clique automatiquement
    ///     quand l'hôte démarre)
    ///
    /// Stratégie :
    ///   1) Walk depuis Camera.main.transform.root (puis depuis le scene root
    ///      si Camera.main absent) en cherchant tous les Button actifs.
    ///   2) Pour chaque Button trouvé, on récupère le Text enfant et on
    ///      compare son contenu (case-insensitive) aux libellés candidats :
    ///         FR : "NOUVELLE PARTIE", "NOUVELLE", "DÉMARRER"
    ///         EN : "NEW GAME", "START"
    ///         RU : "НОВАЯ ИГРА", "НОВАЯ"
    ///   3) Premier match → invoque .onClick.Invoke().
    /// </summary>
    internal static class MenuButtonClicker
    {
        // Libellés candidats : on essaie un large éventail pour supporter les
        // langues les plus probables du jeu (FR, EN, RU). Les libellés sont
        // comparés en uppercase pour la robustesse.
        private static readonly string[] NewGameLabels =
        {
            "NOUVELLE PARTIE", "NOUVELLE PARTIE",
            "NEW GAME", "NEW",
            "НОВАЯ ИГРА", "НОВАЯ",
            "NUEVA PARTIDA", "NUEVA",
            "DÉMARRER", "DEMARRER", "START",
        };

        /// <summary>
        /// Cherche et clique le bouton "Nouvelle Partie" du menu MiSide.
        /// Retourne true si un bouton a été trouvé et invoqué, false sinon.
        /// </summary>
        public static bool ClickNewGame()
        {
            try
            {
                var btn = FindNewGameButton();
                if (btn == null)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        "[Co-op] MenuButtonClicker.ClickNewGame: 'Nouvelle Partie' button not found. "
                      + "Are we in the MiSide main menu? Candidates tried: "
                      + string.Join(", ", NewGameLabels));
                    return false;
                }
                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] MenuButtonClicker: invoking 'Nouvelle Partie' button "
                  + $"('{ReadText(btn)}').");
                try { btn.onClick.Invoke(); }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogError(
                        $"[Co-op] MenuButtonClicker: onClick.Invoke threw: {ex.Message}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogError(
                    $"[Co-op] MenuButtonClicker.ClickNewGame failed: {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Implémentation
        // ─────────────────────────────────────────────────────────────────────
        private static Button FindNewGameButton()
        {
            // Anchor : Camera.main.transform.root si dispo (le menu MiSide est
            // dans la scène active, Camera.main est la caméra du menu).
            Transform anchor = null;
            try
            {
                var cam = Camera.main;
                if (cam != null) anchor = cam.transform.root;
            }
            catch { }

            // Fallback : on tente Find sur quelques noms classiques de Canvas
            // pour ratisser le menu (utile si Camera.main est null).
            if (anchor == null)
            {
                foreach (var n in new[] { "Canvas", "Menu", "MainMenu", "MenuPrincipal" })
                {
                    var go = GameObject.Find(n);
                    if (go != null) { anchor = go.transform; break; }
                }
            }
            if (anchor == null) return null;

            Button[] buttons;
            try { buttons = anchor.GetComponentsInChildrenSafe<Button>(true); }
            catch { return null; }
            if (buttons == null || buttons.Length == 0) return null;

            foreach (var b in buttons)
            {
                if (b == null) continue;
                var txt = ReadText(b);
                if (string.IsNullOrEmpty(txt)) continue;
                var up = txt.Trim().ToUpperInvariant();
                foreach (var cand in NewGameLabels)
                {
                    var cu = cand.ToUpperInvariant();
                    if (up == cu || up.Contains(cu))
                        return b;
                }
            }
            return null;
        }

        private static string ReadText(Button b)
        {
            try
            {
                if (b == null) return null;
                var t = b.GetComponentInChildrenSafe<Text>(true);
                return t?.text;
            }
            catch { return null; }
        }
    }
}

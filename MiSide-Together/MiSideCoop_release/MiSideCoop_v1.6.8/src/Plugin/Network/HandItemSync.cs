using System;
using System.Collections.Generic;
using UnityEngine;
using MiSideCoop.Avatars;

namespace MiSideCoop.Network
{
    /// <summary>
    /// v1.6.8 — Synchronisation d'activation des hand-items (Smartphone, Knife,
    /// Tetris, GameBoy, Flashlight…) entre les deux joueurs.
    ///
    /// Pourquoi ce composant ?
    ///   Dans MiSide, plusieurs séquences scriptées activent un objet "hand-item"
    ///   localement (téléphone Mita en début de jeu, couteau plus tard, etc.).
    ///   Le pickup se fait via cutscene/dialogue, pas via PlayerMove.TakeItem(),
    ///   donc nos Harmony hooks ne détectent pas l'événement. Conséquence en
    ///   co-op : un seul des deux joueurs voit l'item dans ses mains, l'autre
    ///   reste les mains vides (bug v1.6.x).
    ///
    /// Architecture v1.6.8 :
    ///   • POLL : on parcourt à intervalle régulier (200 ms) la liste de paths
    ///     1ère-personne connus (ex: "Player/RightItem FixPosition/Smartphone")
    ///     et la liste de paths 3ème-personne (ex: child "Smartphone" sous
    ///     l'os Right hand).
    ///   • CHANGE DETECTION : on garde un cache du dernier état activeInHierarchy
    ///     par item. Au changement, on broadcast un HandItemMessage.
    ///   • APPLY (receiver) : on retrouve LE MÊME path 1ère-personne dans notre
    ///     scène locale et on lui applique SetActive. On retrouve aussi le child
    ///     "Right item" sur le CLONE 3D représentant le sender (Player2_Guest
    ///     sur le host, Player1_Host_Remote sur le guest) et on l'active.
    ///
    /// Résultat : les DEUX joueurs voient l'item dans leurs propres mains
    /// (1ère personne) ET sur le clone du peer (3ème personne).
    /// </summary>
    public class HandItemSync : MonoBehaviour
    {
        public static HandItemSync Instance { get; private set; }

        // Liste des items à monitorer. Pour chaque item :
        //   • Name : identifiant court réseau (ex: "Smartphone")
        //   • FpPaths : paths candidats du GO 1ère-personne (premier match utilisé)
        //   • TpChildNames : noms de child GameObject candidats à activer sous
        //     l'os Right hand (3ème personne, sur Person et sur clone)
        private static readonly HandItemDef[] Items =
        {
            new HandItemDef
            {
                Name = "Smartphone",
                FpPaths = new[]
                {
                    "Player/RightItem FixPosition/Smartphone",
                    "GameController/Player/RightItem FixPosition/Smartphone",
                },
                TpChildNames = new[] { "Smartphone", "Phone" },
            },
            new HandItemDef
            {
                Name = "Knife",
                FpPaths = new[]
                {
                    "Player/RightItem FixPosition/Knife",
                    "GameController/Player/RightItem FixPosition/Knife",
                },
                TpChildNames = new[] { "Knife" },
            },
            new HandItemDef
            {
                Name = "Tetris",
                FpPaths = new[]
                {
                    "Player/RightItem FixPosition/Tetris",
                    "GameController/Player/RightItem FixPosition/Tetris",
                    "Player/RightItem FixPosition/GameBoy",
                    "GameController/Player/RightItem FixPosition/GameBoy",
                },
                TpChildNames = new[] { "Tetris", "GameBoy" },
            },
            new HandItemDef
            {
                Name = "Flashlight",
                FpPaths = new[]
                {
                    "Player/RightItem FixPosition/Flashlight",
                    "GameController/Player/RightItem FixPosition/Flashlight",
                    "Player/RightItem FixPosition/Torch",
                },
                TpChildNames = new[] { "Flashlight", "Torch" },
            },
        };

        private const float PollInterval = 0.2f;     // 5 Hz suffisant pour un toggle
        private const float RescanInterval = 2.0f;   // re-Find les GO toutes les 2 s

        private float _pollTimer;
        private float _rescanTimer;

        // Cache des GO 1ère-personne résolus localement (par ItemName).
        private readonly Dictionary<string, GameObject> _fpCache = new Dictionary<string, GameObject>();
        // Cache du dernier état actif observé localement (par ItemName).
        private readonly Dictionary<string, bool> _fpLastActive = new Dictionary<string, bool>();

        // ── Lifecycle ────────────────────────────────────────────────────────
        private void Awake()
        {
            if (Instance != null) { Destroy(this); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            var nm = CoopNetworkManager.Instance;
            if (nm == null || !nm.IsConnected) return;

            _rescanTimer -= Time.deltaTime;
            if (_rescanTimer <= 0f)
            {
                _rescanTimer = RescanInterval;
                _fpCache.Clear(); // force la résolution au prochain poll
            }

            _pollTimer -= Time.deltaTime;
            if (_pollTimer > 0f) return;
            _pollTimer = PollInterval;

            try { PollAndBroadcast(); }
            catch (Exception ex)
            {
                if (Time.frameCount % 600 == 0)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        $"[HandItemSync] poll error: {ex.Message}");
                }
            }
        }

        // ── Poll local + broadcast au peer ───────────────────────────────────
        private void PollAndBroadcast()
        {
            foreach (var def in Items)
            {
                GameObject go;
                if (!_fpCache.TryGetValue(def.Name, out go) || go == null)
                {
                    go = ResolveFirstPersonGo(def);
                    _fpCache[def.Name] = go; // stocke même si null pour éviter le scan permanent
                }
                if (go == null) continue;

                bool nowActive;
                try { nowActive = go.activeInHierarchy; }
                catch { continue; }

                bool wasActive;
                if (!_fpLastActive.TryGetValue(def.Name, out wasActive))
                {
                    _fpLastActive[def.Name] = nowActive;
                    continue; // 1er sample : on ne broadcast pas (état initial)
                }
                if (wasActive == nowActive) continue;

                _fpLastActive[def.Name] = nowActive;
                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[HandItemSync] Local hand-item '{def.Name}' changed → active={nowActive}. Broadcasting.");
                CoopNetworkManager.Instance?.BroadcastHandItem(new HandItemMessage
                {
                    ItemName = def.Name,
                    IsActive = nowActive,
                    Slot     = "FP",
                });
            }
        }

        // ── Apply state received from peer ──────────────────────────────────
        public void ApplyRemote(HandItemMessage msg)
        {
            if (string.IsNullOrEmpty(msg.ItemName)) return;
            var def = FindDef(msg.ItemName);
            if (def == null)
            {
                MiSideCoopPlugin.Logger?.LogDebug(
                    $"[HandItemSync] Unknown item '{msg.ItemName}' — ignored.");
                return;
            }

            // 1) Active la version 1ère-personne LOCALE (même path que chez le
            //    sender, on est dans la même scène MiSide donc la hiérarchie
            //    est identique).
            try
            {
                var fp = ResolveFirstPersonGo(def);
                if (fp != null)
                {
                    if (fp.activeSelf != msg.IsActive)
                    {
                        fp.SetActive(msg.IsActive);
                        // mémorise l'état pour ne pas re-broadcast une simple
                        // application réseau qui re-déclencherait un cycle.
                        _fpLastActive[def.Name] = msg.IsActive;
                        MiSideCoopPlugin.Logger?.LogInfo(
                            $"[HandItemSync] Applied remote {def.Name} → 1st-person active={msg.IsActive}.");
                    }
                }
                else
                {
                    MiSideCoopPlugin.Logger?.LogDebug(
                        $"[HandItemSync] Remote {def.Name}: no local 1st-person GO found yet.");
                }
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[HandItemSync] Apply 1st-person {def.Name}: {ex.Message}");
            }

            // 2) Active le 3rd-person Right item sur le CLONE qui représente le
            //    sender (Player2_Guest sur host, Player1_Host_Remote sur guest).
            try
            {
                var cloneRoot = FindRemoteCloneRoot();
                if (cloneRoot != null)
                {
                    var tpGo = FindThirdPersonChild(cloneRoot.transform, def.TpChildNames);
                    if (tpGo != null && tpGo.activeSelf != msg.IsActive)
                    {
                        tpGo.SetActive(msg.IsActive);
                        MiSideCoopPlugin.Logger?.LogInfo(
                            $"[HandItemSync] Applied remote {def.Name} → 3rd-person on clone active={msg.IsActive}.");
                    }
                }
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[HandItemSync] Apply 3rd-person {def.Name}: {ex.Message}");
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private static HandItemDef FindDef(string name)
        {
            foreach (var d in Items)
            {
                if (d.Name == name) return d;
            }
            return null;
        }

        private static GameObject ResolveFirstPersonGo(HandItemDef def)
        {
            foreach (var p in def.FpPaths)
            {
                GameObject g = null;
                try { g = GameObject.Find(p); } catch { }
                if (g != null) return g;
            }
            return null;
        }

        // Cherche dans la hiérarchie du clone un GameObject dont le nom matche
        // un des candidats. On part de la racine du clone et on descend
        // récursivement. Profondeur limitée pour éviter les boucles.
        private static GameObject FindThirdPersonChild(Transform root, string[] candidates)
        {
            if (root == null || candidates == null || candidates.Length == 0) return null;
            return SearchChildByName(root, candidates, 0, 24);
        }

        private static GameObject SearchChildByName(Transform t, string[] candidates, int depth, int maxDepth)
        {
            if (t == null || depth > maxDepth) return null;
            string n = null;
            try { n = t.gameObject.name; } catch { }
            if (!string.IsNullOrEmpty(n))
            {
                foreach (var c in candidates)
                    if (string.Equals(n, c, StringComparison.OrdinalIgnoreCase))
                        return t.gameObject;
            }
            int childCount;
            try { childCount = t.childCount; } catch { return null; }
            for (int i = 0; i < childCount; i++)
            {
                Transform child = null;
                try { child = t.GetChild(i); } catch { continue; }
                var found = SearchChildByName(child, candidates, depth + 1, maxDepth);
                if (found != null) return found;
            }
            return null;
        }

        // Renvoie la racine du clone 3D qui représente le PEER côté ce client.
        // Sur l'host : Player2_Guest. Sur le guest : Player1_Host_Remote.
        private static GameObject FindRemoteCloneRoot()
        {
            // On cherche par nom direct dans la scène — le clone est root et
            // marqué DontDestroyOnLoad, donc GameObject.Find le retrouve.
            GameObject g = null;
            try { g = GameObject.Find("Player2_Guest"); } catch { }
            if (g != null) return g;
            try { g = GameObject.Find("Player1_Host_Remote"); } catch { }
            return g;
        }

        private class HandItemDef
        {
            public string Name;
            public string[] FpPaths;
            public string[] TpChildNames;
        }
    }
}

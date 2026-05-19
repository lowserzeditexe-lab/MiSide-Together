using System;
using UnityEngine;

namespace MiSideCoop.Avatars
{
    /// <summary>
    /// v1.5.0 — Clone le GameObject du MC local de MiSide pour produire un
    /// "fantôme" 3D réaliste qu'on utilise comme représentation visuelle du
    /// peer, à la place du capsule + sphère synthétique des v1.3.x — v1.4.x.
    ///
    /// v1.5.3 — Clone 'Person' (sous-GO 3rd-person body) plutôt que 'Player'
    /// entier, pour éviter le double Animator (Player Arms first-person vide
    /// vs Person 3rd-person avec controller MiSide).
    ///
    /// v1.5.4 — Prépare l'Animator du ghost pour qu'il joue correctement :
    ///   • cullingMode = AlwaysAnimate (évite le gel du ghost hors-champ)
    ///   • applyRootMotion = false (évite le fight contre notre Lerp réseau)
    ///   • enabled = true (défensif)
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

                // v1.5.3 — Clone le sous-GO "Person" si présent (3rd-person body
                // avec controller MiSide). Sinon clone Player entier.
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

                Vector3    srcPos = cloneSource.transform.position;
                Quaternion srcRot = cloneSource.transform.rotation;

                // v1.6.5 — INACTIVE-HOLDER PATTERN POUR BLOQUER L'AWAKE DES HAND-ITEMS
                //
                // Problème v1.6.4 : si la source a un hand-item ACTIF au moment du
                // clone (ex: l'utilisateur tient le téléphone), Instantiate copie
                // l'état actif → Awake() fire IMMÉDIATEMENT sur le clone pendant
                // Object.Instantiate → singleton override avant qu'on puisse réagir
                // → l'objet local "saute" sur la main du ghost (cf. screenshot user
                // v1.6.4 : Phone visible sur le ghost au lieu du host).
                //
                // Solution : Instantiate dans un parent INACTIF (SetActive(false)).
                // En Unity, les composants d'un GameObject dont activeInHierarchy
                // est false n'exécutent PAS Awake/OnEnable. On peut alors désactiver
                // les hand-items un par un AVANT d'unparent → quand on déparente le
                // clone (qui s'active), seuls les non-hand-items font leur Awake.
                //
                // Différence vs v1.6.1 : on utilise SetActive(false) plutôt que
                // DestroyImmediate. SetActive n'enlève AUCUN GO ni script, donc
                // pas de régression sur les enfants accessoires (head/arms/etc.)
                // qui auraient pu être (mal-)parentés sous les hand-items.
                // Le StripHandItems(safety-net) en fin de StripInterferingComponents
                // se chargera ensuite de Destroy(GO) (sur des GOs déjà inactifs,
                // donc 100% safe — aucun Awake n'aura tourné).
                GameObject holder = null;
                GameObject clone;
                try
                {
                    holder = new GameObject("__MiSideCoopCloneHolder__");
                    holder.SetActive(false);
                    clone = UnityEngine.Object.Instantiate(cloneSource, holder.transform);
                    if (clone == null)
                    {
                        MiSideCoopPlugin.Logger?.LogWarning(
                            "[Co-op] RealMcCloner: Instantiate returned null. Fall back to synthetic.");
                        try { UnityEngine.Object.Destroy(holder); } catch { }
                        return null;
                    }
                    clone.name = goName;

                    // Désactive les hand-items du clone PENDANT qu'il est dans
                    // le holder inactif (donc avant tout Awake). Quand on
                    // unparente le clone juste après, ces GOs resteront inactifs
                    // → leur Awake/OnEnable ne se déclencheront jamais.
                    DeactivateHandItemsInHierarchy(clone);

                    // Déparente vers la scène (le clone devient actif → Awake
                    // fire sur les composants non-hand-item).
                    clone.transform.SetParent(null, true);
                }
                finally
                {
                    if (holder != null)
                    {
                        try { UnityEngine.Object.Destroy(holder); } catch { }
                    }
                }

                clone.transform.position = srcPos + new Vector3(1.2f, 0f, 0f);
                clone.transform.rotation = srcRot;

                // Strip défensif + re-activation + re-layer + prep animator.
                StripInterferingComponents(clone);
                PrepareGhostAnimator(clone);

                // v1.6.0 — Sort le clone de la T-pose initiale en forçant un
                // Rebind + Update(0) sur tous les Animators. Sans ça, l'Animator
                // cloné par Instantiate démarre parfois sur l'état Entry sans
                // avoir évalué le default state du controller → bind pose = T-pose.
                try
                {
                    var anims = clone.transform.GetComponentsInChildrenSafe<Animator>(true);
                    foreach (var a in anims)
                    {
                        if (a == null) continue;
                        try { a.Rebind();     } catch { }
                        try { a.Update(0f);  } catch { }
                    }
                    MiSideCoopPlugin.Logger?.LogInfo(
                        $"[Co-op] RealMcCloner: kicked {anims.Length} animator(s) out of T-pose "
                      + "(Rebind + Update(0) — v1.6.0).");
                }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        $"[Co-op] RealMcCloner.RebindKick: {ex.Message}");
                }

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

        // ── v1.5.4 — Préparation de l'Animator du ghost ──────────────────────
        //
        // v1.5.6 — Énumère AUSSI les paramètres de l'Animator et les pousse
        // dans AvatarAnimatorSync.CachedAnimatorParams. En IL2CPP MiSide,
        // parameterCount + GetParameter(int) sont STRIPPÉS quand appelés
        // depuis une MonoBehaviour Il2Cpp-registered (= AvatarAnimatorSync)
        // mais MARCHENT depuis un helper static plain managed (= ICI). On
        // exploite cette différence pour récupérer les vrais noms des params
        // (en russe en MiSide) et les passer à AvatarAnimatorSync.
        private static void PrepareGhostAnimator(GameObject root)
        {
            try
            {
                var animators = root.transform.GetComponentsInChildrenSafe<Animator>(true);
                int prepared = 0;
                foreach (var anim in animators)
                {
                    if (anim == null) continue;
                    try { anim.cullingMode    = AnimatorCullingMode.AlwaysAnimate; } catch { }
                    try { anim.applyRootMotion = false; }                            catch { }
                    try { anim.enabled        = true; }                              catch { }
                    prepared++;
                }

                // Détail du premier Animator + énumération static des params.
                if (animators.Length > 0)
                {
                    // v1.6.8 — Sélectionne le MEILLEUR Animator pour l'énumération
                    // des params : celui qui a un runtimeAnimatorController valide
                    // ET au moins 1 paramètre. Sans ce filtre, animators[0]
                    // pouvait être le "Player Arms" Animator (controller='', 0 params)
                    // → EnumerateAndCacheParams faisait 15 NullReferenceException
                    // en boucle (cf. log user v1.6.7). On préfère un GO nommé
                    // 'Person*' ou un root renommé clone (Player2_Guest, Player1_Host_Remote).
                    Animator main = PickBestAnimatorForEnum(animators);
                    if (main == null)
                    {
                        MiSideCoopPlugin.Logger?.LogWarning(
                            "[Co-op] RealMcCloner.PrepareGhostAnimator: no Animator with a valid "
                          + "controller found — skipping param enumeration to avoid NullRef spam.");
                        return;
                    }
                    try
                    {
                        string ctrl = main.runtimeAnimatorController != null
                            ? main.runtimeAnimatorController.name
                            : "<null>";
                        int layers = -1, pcount = -1;
                        try { layers = main.layerCount; }     catch { }
                        try { pcount = main.parameterCount; } catch { }
                        MiSideCoopPlugin.Logger?.LogInfo(
                            $"[Co-op] RealMcCloner.PrepareGhostAnimator: {prepared} animator(s) configured. "
                          + $"Main animator on '{main.gameObject.name}' — controller='{ctrl}', "
                          + $"layers={layers}, params={pcount}, cullingMode={main.cullingMode}, "
                          + $"applyRootMotion={main.applyRootMotion}, enabled={main.enabled}.");

                        // v1.5.6 — Énumère et cache pour AvatarAnimatorSync.
                        EnumerateAndCacheParams(main);
                    }
                    catch (Exception ex)
                    {
                        MiSideCoopPlugin.Logger?.LogWarning(
                            $"[Co-op] RealMcCloner.PrepareGhostAnimator: detail/enum log failed: {ex.Message}");
                    }
                }
                else
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        "[Co-op] RealMcCloner.PrepareGhostAnimator: NO Animator found on clone hierarchy. "
                      + "Animations will not play. Source MC may have its Animator at an unexpected path.");
                }
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] RealMcCloner.PrepareGhostAnimator: {ex.Message}");
            }
        }

        /// <summary>
        /// v1.6.8 — Sélectionne le meilleur Animator pour l'énumération des
        /// paramètres : priorité aux GO 'Person*' / 'Player2_Guest' / 'Player1_Host_Remote',
        /// sinon le premier Animator avec parameterCount > 0.
        /// Renvoie null si AUCUN candidat n'est valide → l'appelant skip l'énumération
        /// pour ne pas faire 15 NullReferenceException sur GetParameter(i).
        ///
        /// v1.6.9 — IMPORTANT : en IL2CPP MiSide, l'accesseur
        /// `runtimeAnimatorController` retourne null même quand un controller
        /// VALIDE est attaché (property stripping). On ne peut donc PAS s'en
        /// servir comme critère de filtrage. À la place on regarde
        /// `parameterCount > 0` qui, lui, marche dans IL2CPP MiSide.
        /// Pour 'Player Arms' (controller vide réel) parameterCount = 0
        /// donc il est exclu naturellement.
        /// </summary>
        private static Animator PickBestAnimatorForEnum(Animator[] all)
        {
            if (all == null || all.Length == 0) return null;
            Animator best = null;
            foreach (var a in all)
            {
                if (a == null) continue;
                int pc = 0;
                try { pc = a.parameterCount; } catch { }
                if (pc <= 0) continue;
                string goName = string.Empty;
                try { goName = a.gameObject.name; } catch { }
                if (!string.IsNullOrEmpty(goName) &&
                    (goName.StartsWith("Person")
                     || goName == "Player2_Guest"
                     || goName == "Player1_Host_Remote"))
                {
                    return a;
                }
                if (best == null) best = a;
            }
            return best;
        }

        /// <summary>
        /// v1.5.6 — Énumère les paramètres de l'Animator depuis un contexte
        /// static plain managed (le seul où ça n'est pas strippé en IL2CPP MiSide)
        /// et pousse les noms dans AvatarAnimatorSync.CachedAnimatorParams pour
        /// que l'instance MonoBehaviour les retrouve sans re-énumération.
        /// </summary>
        private static void EnumerateAndCacheParams(Animator anim)
        {
            try
            {
                int animId = anim.GetInstanceID();
                int count;
                try { count = anim.parameterCount; }
                catch (Exception ex)
                {
                    MiSideCoopPlugin.Logger?.LogWarning(
                        $"[Co-op]   EnumerateAndCacheParams: parameterCount stripped ({ex.Message}). "
                      + "AvatarAnimatorSync will fall back to English-name candidates.");
                    return;
                }

                var pset = Network.AvatarAnimatorSync.CachedAnimatorParams.GetOrCreate(animId);
                pset.Floats.Clear();
                pset.Bools.Clear();
                pset.Triggers.Clear();
                pset.Ints.Clear();

                int okFloats = 0, okBools = 0, okTriggers = 0, okInts = 0, failures = 0;
                for (int i = 0; i < count; i++)
                {
                    UnityEngine.AnimatorControllerParameter p = null;
                    try { p = anim.GetParameter(i); }
                    catch { failures++; continue; }
                    if (p == null) { failures++; continue; }

                    string name = null;
                    UnityEngine.AnimatorControllerParameterType type = default;
                    try { name = p.name; type = p.type; }
                    catch { failures++; continue; }
                    if (string.IsNullOrEmpty(name)) continue;

                    switch (type)
                    {
                        case UnityEngine.AnimatorControllerParameterType.Float:
                            pset.Floats.Add(name); okFloats++; break;
                        case UnityEngine.AnimatorControllerParameterType.Bool:
                            pset.Bools.Add(name); okBools++; break;
                        case UnityEngine.AnimatorControllerParameterType.Trigger:
                            pset.Triggers.Add(name); okTriggers++; break;
                        case UnityEngine.AnimatorControllerParameterType.Int:
                            pset.Ints.Add(name); okInts++; break;
                    }
                }

                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op]   EnumerateAndCacheParams: animId={animId}, total={count}, "
                  + $"floats={okFloats}{(okFloats > 0 ? " [" + string.Join(",", pset.Floats) + "]" : "")}, "
                  + $"bools={okBools}{(okBools > 0 ? " [" + string.Join(",", pset.Bools) + "]" : "")}, "
                  + $"triggers={okTriggers}{(okTriggers > 0 ? " [" + string.Join(",", pset.Triggers) + "]" : "")}, "
                  + $"ints={okInts}, failures={failures}.");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op]   EnumerateAndCacheParams: {ex.Message}");
            }
        }

        // ── Strip défensif ────────────────────────────────────────────────────
        // On supprime tout ce qui peut "vivre sa vie" sur le clone et entrer
        // en conflit avec le MC local. On garde l'Animator (utilisé par
        // AvatarAnimatorSync) et la hiérarchie de mesh.
        private static void StripInterferingComponents(GameObject root)
        {
            // v1.5.8 - StripHandItems was moved to AFTER the re-activation step
            // (search for "StripHandItems v1.5.8" below) because v1.5.1's
            // re-activation re-enables previously-hidden hand items (Tetris MR
            // was inactive in the local MC because the player is not in a
            // mini-game). Doing the strip first means v1.5.1 then re-activates
            // the items we just hid. Now we strip AFTER everything else.

            // v1.5.3 - RE-LAYER to 0 (default) so all cameras see the clone.
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

            // v1.5.3 — Material audit pour éviter le pink fluo Unity.
            try
            {
                var smrs = root.transform.GetComponentsInChildrenSafe<SkinnedMeshRenderer>(true);
                Material referenceMat = null;
                int nullCount = 0, fixedCount = 0;

                foreach (var smr in smrs)
                {
                    if (smr == null) continue;
                    try
                    {
                        if (smr.sharedMaterial != null) { referenceMat = smr.sharedMaterial; break; }
                    }
                    catch { }
                }

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

            // v1.5.1 — Ré-activation des GameObjects désactivés (tête, cheveux…)
            //
            // v1.6.4 — FIX CRITIQUE : exclure les hand-items (Tetris, GameBoy,
            // phone, etc.) de cette ré-activation. Sur le MC local, ces GOs
            // sont normalement INACTIFS (l'utilisateur n'est pas en mini-jeu).
            // Instantiate clone cet état → clone hérite hand-item inactif →
            // Awake() ne fire pas → leur singleton (ex: `Tetris.Instance = this`)
            // ne se déclenche pas → le Tetris du joueur local reste référencé.
            //
            // BUG v1.5.1–v1.6.3 : la boucle re-activait AVEUGLÉMENT tous les
            // GOs inactifs, y compris Tetris/Tetris Game. Awake() firait sur
            // le clone → singleton volé → Tetris.Instance pointait sur le
            // clone → quand StripHandItems destroyait le clone, Instance
            // devenait Unity-null → l'objet disparaissait des mains du joueur
            // local (cf. screenshot user v1.6.3 : mains vides en co-op).
            try
            {
                int activated = 0, skippedHand = 0;
                ForEachChildTransform(root.transform, child =>
                {
                    if (child == null || child.gameObject == null) return;
                    if (child.gameObject.activeSelf) return;

                    // v1.6.4 — Skip hand-items pour préserver le singleton local.
                    string n = string.Empty;
                    try { n = child.gameObject.name; } catch { }
                    if (!string.IsNullOrEmpty(n))
                    {
                        var lo = n.ToLowerInvariant();
                        foreach (var kw in HandItemKeywords)
                        {
                            if (lo.Contains(kw))
                            {
                                skippedHand++;
                                return; // ne ré-active PAS — Awake ne firera pas
                            }
                        }
                    }

                    try { child.gameObject.SetActive(true); activated++; } catch { }
                });
                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] RealMcCloner: re-activated {activated} hidden child GameObject(s) on clone "
                  + $"(skipped {skippedHand} hand-item(s) to preserve local singleton — v1.6.4).");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] RealMcCloner.ReactivateChildren: {ex.Message}");
            }

            // v1.4.10 — Ré-activation des renderers cachés.
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

                // v1.5.4 — Force aussi le refresh des bounds des SMR.
                // Sans ça, les bounds peuvent être héritées "vides" du prefab
                // source, et Unity culle le SMR → Animator passé en CullUpdateTransforms.
                // updateWhenOffscreen = true coûte un peu de CPU mais garantit
                // que le ghost est toujours visible et animé.
                foreach (var smr in skinned)
                {
                    if (smr == null) continue;
                    try { smr.updateWhenOffscreen = true; } catch { }
                }

                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] RealMcCloner: enabled {meshRends.Length} MeshRenderer(s) and "
                  + $"{skinned.Length} SkinnedMeshRenderer(s) on clone (incl. head/hair/face, "
                  + "updateWhenOffscreen=true for anim culling fix).");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] RealMcCloner.EnableAllRenderers: {ex.Message}");
            }

            // 1) Caméras enfants : désactivées (Destroy peut crasher via FlareLayer).
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

            // 2) AudioListeners enfants : désactivés.
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

            // 3) CharacterController désactivé + effacé.
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

            // 4) Rigidbody → kinematic.
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

            // 5) Colliders → trigger.
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

            // 6) MonoBehaviour custom MiSide → désactivés (sauf nos propres composants).
            try
            {
                var mbs = root.transform.GetComponentsInChildrenSafe<MonoBehaviour>(true);
                foreach (var mb in mbs)
                {
                    if (mb == null) continue;
                    var t = mb.GetType();
                    string ns = t.Namespace ?? string.Empty;
                    if (ns.StartsWith("MiSideCoop")) continue;
                    try { mb.enabled = false; } catch { }
                }
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] RealMcCloner.StripMonoBehaviours: {ex.Message}");
            }

            // ============================================================
            // StripHandItems v1.6.1 — SAFETY NET seulement
            // ============================================================
            // Le vrai strip se fait dans DestroyHandItemsBeforeAwake() AVANT
            // que le clone soit activé (suppress Awake → pas d'override
            // singleton). Ce strip post-activation reste comme safety-net
            // au cas où un hand-item aurait été créé dynamiquement par un
            // script entre l'activation et ce point (rare).
            try
            {
                string[] handItemKeywords = HandItemKeywords;
                int strippedItems = 0;
                ForEachChildTransform(root.transform, child =>
                {
                    if (child == null || child.gameObject == null) return;
                    if (child == root.transform) return;
                    var n = child.gameObject.name;
                    if (string.IsNullOrEmpty(n)) return;
                    var lo = n.ToLowerInvariant();
                    foreach (var kw in handItemKeywords)
                    {
                        if (lo.Contains(kw))
                        {
                            try
                            {
                                child.gameObject.SetActive(false);
                                try { UnityEngine.Object.Destroy(child.gameObject); } catch { }
                                strippedItems++;
                                MiSideCoopPlugin.Logger?.LogInfo(
                                    $"[Co-op]   StripHandItems(safety-net): destroyed '{n}' (kw='{kw}').");
                            }
                            catch { }
                            break;
                        }
                    }
                });
                if (strippedItems > 0)
                {
                    MiSideCoopPlugin.Logger?.LogInfo(
                        $"[Co-op] RealMcCloner: safety-net destroyed {strippedItems} late hand-item(s).");
                }
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] RealMcCloner.StripHandItems(safety-net): {ex.Message}");
            }
        }

        // v1.6.1 — Liste centralisée des mots-clés hand-item.
        // Conservée minimale pour éviter de matcher des bones (ex: "Hand_R"
        // doit rester, donc on ne met PAS "hand" tout court).
        private static readonly string[] HandItemKeywords =
        {
            "gameboy", "tetris", "phone", "device", "console",
            "handitem", "righthand_item", "lefthand_item",
            "item_r", "item_l", "holdingitem", "carry"
        };

        // v1.6.5 — Désactive (SetActive(false)) tous les hand-items dans la
        // hiérarchie du clone. Doit être appelé PENDANT que le clone est dans
        // le holder inactif (avant tout Awake). Garantit que ces GOs restent
        // inactifs après l'unparent → leur Awake/OnEnable ne fire jamais.
        //
        // Différence avec DestroyHandItemsBeforeAwake : on ne SUPPRIME pas le
        // GO (qui pourrait contenir des enfants accessoires mal-parentés —
        // régression v1.6.1), on le rend juste inactif. Le safety-net
        // StripHandItems en fin de StripInterferingComponents Destroy()era
        // ensuite ces GOs sans risque (déjà inactifs).
        private static void DeactivateHandItemsInHierarchy(GameObject clone)
        {
            if (clone == null) return;
            try
            {
                int deactivated = 0, alreadyInactive = 0;
                ForEachChildTransform(clone.transform, child =>
                {
                    if (child == null || child.gameObject == null) return;
                    if (child == clone.transform) return;
                    var n = child.gameObject.name;
                    if (string.IsNullOrEmpty(n)) return;
                    var lo = n.ToLowerInvariant();
                    foreach (var kw in HandItemKeywords)
                    {
                        if (lo.Contains(kw))
                        {
                            if (child.gameObject.activeSelf)
                            {
                                try { child.gameObject.SetActive(false); deactivated++; } catch { }
                            }
                            else
                            {
                                alreadyInactive++;
                            }
                            break;
                        }
                    }
                });
                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] RealMcCloner: deactivated {deactivated} active hand-item(s) "
                  + $"+ {alreadyInactive} already-inactive hand-item(s) on clone — "
                  + "Awake suppressed, source singletons preserved (v1.6.5).");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] RealMcCloner.DeactivateHandItemsInHierarchy: {ex.Message}");
            }
        }

        // v1.6.1 — Détruit les hand-items du clone AVANT son activation,
        // donc AVANT que leurs Awake() / OnEnable() ne soient appelés.
        // C'est la clé du fix "Gameboy invisible" : si un script Tetris
        // fait `Instance = this` dans Awake, le clone overriderait Instance.
        // En détruisant le GO AVANT Awake, ce script n'existe plus.
        //
        // Appelé pendant que `clone.activeInHierarchy == false` (clone est
        // dans un holder désactivé).
        private static void DestroyHandItemsBeforeAwake(GameObject clone)
        {
            if (clone == null) return;
            try
            {
                int count = 0;
                var toDestroy = new System.Collections.Generic.List<GameObject>();
                ForEachChildTransform(clone.transform, child =>
                {
                    if (child == null || child.gameObject == null) return;
                    if (child == clone.transform) return;
                    var n = child.gameObject.name;
                    if (string.IsNullOrEmpty(n)) return;
                    var lo = n.ToLowerInvariant();
                    foreach (var kw in HandItemKeywords)
                    {
                        if (lo.Contains(kw))
                        {
                            toDestroy.Add(child.gameObject);
                            break;
                        }
                    }
                });
                foreach (var go in toDestroy)
                {
                    if (go == null) continue;
                    string nm = string.Empty;
                    try { nm = go.name; } catch { }
                    try
                    {
                        // DestroyImmediate ici car on est dans un contexte
                        // d'init synchrone, et le clone n'a pas encore tourné
                        // une frame. C'est documenté safe par Unity dans ce
                        // cas précis (objet pas activé).
                        UnityEngine.Object.DestroyImmediate(go);
                        count++;
                    }
                    catch
                    {
                        // Fallback Destroy (asynchrone) si DestroyImmediate
                        // est restreint (ex: appelé pendant un OnValidate).
                        try { UnityEngine.Object.Destroy(go); count++; } catch { }
                    }
                    if (!string.IsNullOrEmpty(nm))
                    {
                        MiSideCoopPlugin.Logger?.LogInfo(
                            $"[Co-op]   DestroyHandItemsBeforeAwake: removed '{nm}' before Awake (v1.6.1).");
                    }
                }
                MiSideCoopPlugin.Logger?.LogInfo(
                    $"[Co-op] RealMcCloner: pre-Awake destroyed {count} hand-item(s) — "
                  + "singletons on the source remain intact (v1.6.1).");
            }
            catch (Exception ex)
            {
                MiSideCoopPlugin.Logger?.LogWarning(
                    $"[Co-op] RealMcCloner.DestroyHandItemsBeforeAwake: {ex.Message}");
            }
        }

        /// <summary>
        /// Walk récursif de la hiérarchie transform. IL2CPP-safe.
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

# MiSide Together — v1.5.4

**Focus :** Fix des animations du fantôme distant (Player2_Guest / Player1_Host_Remote).

Le clone 3D apparaissait correctement depuis la v1.5.3 (mesh, tête, cheveux, textures originales), mais il restait **figé en pose Idle / T-pose** quel que soit le mouvement du peer. La v1.5.4 corrige ce problème avec trois fixes indépendants qui se renforcent mutuellement.

---

## 🐛 Corrections

### 1. Animator du fantôme préparé explicitement à la création
Dans `RealMcCloner.PrepareGhostAnimator()`, on force sur **tous les Animators** du clone :

| Réglage | Avant (hérité) | Après (v1.5.4) | Pourquoi |
|---|---|---|---|
| `cullingMode` | `CullUpdateTransforms` | `AlwaysAnimate` | Évite que Unity gèle l'Animator quand il pense que le `SkinnedMeshRenderer` est hors champ (bounds invalides juste après `Instantiate`). C'est la cause #1 du "fantôme figé en Entry / T-pose". |
| `applyRootMotion` | `true` (MC source) | `false` | Sinon l'Animator essaie de bouger `transform.position` via root motion → fight contre le `Vector3.Lerp` réseau de `PlayerAvatar.Update` → fantôme qui tremble ou se téléporte. |
| `enabled` | (peut être `false`) | `true` | Défensif. |
| SMR `updateWhenOffscreen` | `false` | `true` | Force le refresh des bounds du `SkinnedMeshRenderer` même si la caméra ne le voit pas — évite le culling agressif. |

### 2. `Animator.Play()` utilise `layer = -1` (auto-resolve)
Depuis la v1.5.0, on synchronise les animations via `Animator.Play(fullPathHash, layer, normalizedTime)` en exploitant le fait que le clone et le MC source partagent **le même AnimatorController** (donc les hashes de state sont identiques).

Mais en v1.5.0–v1.5.3, on passait `layer = 0`. Or si MiSide définit l'état dans un layer `UpperBody` ou `LowerBody` (très courant pour les animations de marche/course modulaires), `Play()` faisait un **no-op silencieux** parce que le hash n'existait pas dans le layer 0.

→ v1.5.4 passe `layer = -1` : Unity localise automatiquement le bon layer.

### 3. Fallback **movement-driven** (robustesse cross-version MiSide)
Nouvelle méthode `AvatarAnimatorSync.SetMovementSpeed(float speed)` appelée à chaque message reçu. Elle pousse la vitesse calculée **par delta-position** entre deux messages réseau sur tous les paramètres candidats (`Speed`, `MoveSpeed`, `Velocity`, `BlendSpeed`, ...) **et sur tous les float params découverts dynamiquement** (utile si MiSide utilise des noms russes ou idiosyncratiques).

Avantages :
- Indépendant du nom des paramètres Animator de MiSide.
- Fonctionne même si l'introspection `parameterCount` est strippée IL2CPP (mode best-effort).
- Décroît automatiquement vers 0 si plus de messages reçus depuis 150 ms (le fantôme repasse en idle quand le peer s'arrête).

### 4. Logging diagnostique enrichi
Au spawn du fantôme, on log désormais :
```
[Co-op] RealMcCloner.PrepareGhostAnimator: N animator(s) configured.
        Main animator on 'Person' — controller='Player', layers=2, params=6,
        cullingMode=AlwaysAnimate, applyRootMotion=False, enabled=True.
[Co-op] AnimatorSync.Initialize on 'Person':
        controller='Player', layers=2, params=6 (floats=3, bools=2, triggers=1).
        Speed candidates matched: 1, Walking: 0.
```
Permet de diagnostiquer immédiatement les anims qui ne joueraient pas (controller null, 0 layers, 0 params, ...).

---

## 📦 Fichiers modifiés

- `Plugin/PluginInfo.cs` — version 1.5.3 → 1.5.4
- `Plugin/Network/AvatarAnimatorSync.cs` — `SetMovementSpeed()`, register-all-float-params fallback, logging.
- `Plugin/Network/PlayerAvatar.cs` — delta-position speed estimation, `Animator.Play(hash, -1, normTime)`, decay quand idle.
- `Plugin/Avatars/RealMcCloner.cs` — nouvelle méthode `PrepareGhostAnimator()` appelée après le clone.

---

## 🔄 Compatibilité

- ✅ Compatible avec les sessions co-op **host v1.5.4 ↔ guest v1.5.4**.
- ⚠️ Compatible (downgrade) avec host v1.5.4 ↔ guest v1.5.3 : le fantôme du guest reste figé côté guest (seul le host bénéficie du fix), mais la session ne crashe pas.
- 🆙 **Recommandé : mettre à jour les deux joueurs.** L'AutoUpdater intégré le proposera au démarrage.

---

## 🚀 Installation

Identique aux releases précédentes :
1. Téléchargez `MiSideCoop.dll` depuis les assets de cette release.
2. Copiez-le dans `MiSide/BepInEx/plugins/`.
3. Lancez le jeu — l'overlay `[MiSide Together] Host | ...` doit apparaître en haut à gauche.

L'auto-updater détectera cette version au prochain lancement si vous avez la v1.5.0+.

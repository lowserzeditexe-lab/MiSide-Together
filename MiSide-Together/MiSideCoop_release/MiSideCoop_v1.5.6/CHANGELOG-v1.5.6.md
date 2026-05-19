# MiSide Together — v1.5.6

**Focus :** Fix RÉEL des animations + suppression de la GameBoy en main du fantôme.

La v1.5.4 avait posé les bonnes fondations (cullingMode, applyRootMotion, layer=-1) mais le ghost restait **toujours figé en idle**. Les logs v1.5.5 ont enfin révélé la vraie cause racine.

---

## 🔍 Diagnostic v1.5.5 (logs du peer)

```
[Co-op] RealMcCloner.PrepareGhostAnimator: Main animator on 'Player2_Guest' —
        controller='', layers=2, params=15, cullingMode=AlwaysAnimate, ...
[Warning] AnimatorSync.Initialize: parameter enumeration stripped (Method unstripping failed).
        Falling back to best-effort mode (all English candidates attempted, ...).
```

**Deux faits confirmés :**

1. ✅ L'Animator a bien **15 paramètres** disponibles, lus correctement depuis le **contexte static** de `RealMcCloner.PrepareGhostAnimator`.
2. ❌ Mais quand `AvatarAnimatorSync.Initialize` (MonoBehaviour Il2Cpp-registered) essaye d'énumérer ces mêmes 15 params via `parameterCount`/`GetParameter(int)`, **IL2CPPInterop strippe les méthodes** → on tombe en best-effort avec les noms candidats anglais ("Speed", "IsWalking", ...).

**Or MiSide utilise des noms de params en russe / cyrillique** (cf. les strings cyrilliques dans les logs Unity). Donc **aucun de nos candidats anglais ne match** → `SetFloat`/`SetBool` ne touche AUCUN vrai param → animation figée.

---

## 🐛 Corrections

### 1. Énumération des params via le contexte qui fonctionne

`RealMcCloner.PrepareGhostAnimator` (méthode static, plain managed) appelle désormais `EnumerateAndCacheParams(animator)` :
- Lit `parameterCount` (qui marche ici).
- Pour chaque `i ∈ [0, count)` → `animator.GetParameter(i)` → extrait `name` + `type`.
- Stocke les noms dans un cache statique partagé `AvatarAnimatorSync.CachedAnimatorParams`, indexé par `InstanceID` de l'Animator.

`AvatarAnimatorSync.Initialize` (MonoBehaviour Il2Cpp-registered) vérifie en premier ce cache :
- Si présent → utilise les **vrais noms MiSide** (russes inclus) sans tenter de re-énumérer.
- Si absent → fallback historique (énumération directe puis candidats anglais).

Comme aucun candidat anglais ne matchera, mais **on a maintenant les vrais noms**, on registre **tous les floats** comme cibles `Speed` et **tous les bools** comme gates `Walking`. Quand `SetMovementSpeed(v)` est appelé à chaque message réseau, **une des floats est forcément le Speed du blend tree marche/course** → le ghost s'anime enfin.

### 2. Strip de la GameBoy et autres items en main

Le clone "Person" embarque les sous-GO des items que le MC tient (GameBoy, téléphone, etc.). Nouvelle étape dans `StripInterferingComponents` :
- Liste défensive de keywords (`gameboy`, `tetris`, `phone`, `device`, `console`, `handitem`, `righthand_item`, `lefthand_item`, ...) qu'on cherche dans les noms de GameObjects descendants.
- `SetActive(false)` sur chaque match.
- Logging du nom exact stripé pour pouvoir étendre la liste si besoin.

### 3. Logging enrichi pour diagnostic futur

À chaque clone, un nouveau log :
```
[Co-op]   EnumerateAndCacheParams: animId=12345, total=15, floats=4 [Скорость,Поворот,...],
          bools=3 [Бег,Приседание,...], triggers=8 [...], ints=0, failures=0.
```

→ La prochaine fois qu'il y aura un problème d'anim, on saura **exactement** quels params existent et leurs noms réels.

---

## 📦 Fichiers modifiés

- `Plugin/PluginInfo.cs` — version 1.5.5 → 1.5.6
- `Plugin/Avatars/RealMcCloner.cs` — `EnumerateAndCacheParams()`, `StripHandItems` step
- `Plugin/Network/AvatarAnimatorSync.cs` — `CachedAnimatorParams` static dict + lecture en priorité dans `Initialize`

---

## 🚀 Mise à jour

Si tu as déjà v1.5.5 installé, l'AutoUpdater détectera v1.5.6 et te proposera automatiquement la maj — **cette fois avec la cascade de relance via `run_bepinex.bat` qui marche**, donc BepInEx restera bien injecté après l'update. 🎉

Sinon : télécharge `MiSideCoop.dll` depuis cette release et place-le dans `MiSide/BepInEx/plugins/`.

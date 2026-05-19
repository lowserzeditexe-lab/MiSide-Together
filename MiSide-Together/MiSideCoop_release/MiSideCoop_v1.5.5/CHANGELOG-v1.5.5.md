# MiSide Together — v1.5.5

**Focus :** Fix des bugs du système d'auto-update.

La v1.5.4 livrait correctement le DLL des animations, **mais le système d'auto-update lui-même avait deux bugs** qui empêchaient l'installation propre et la relance du jeu avec BepInEx :

---

## 🐛 Corrections

### 1. Relance du jeu via `run_bepinex.bat` au lieu de `MiSide.exe` direct

**Bug** : depuis la v1.0, le script PowerShell de l'AutoUpdater relançait `MiSide.exe` directement après extraction. Mais beaucoup d'installations BepInEx 6 IL2CPP utilisent **Steam Launch Options** `run_bepinex.bat %command%` pour injecter BepInEx (méthode officielle pour Proton, et fréquente sur Windows aussi). Quand on lance `MiSide.exe` direct, **ces Launch Options sont contournées → BepInEx pas injecté → le jeu redémarre vanilla, sans le mod**.

**Fix v1.5.5** : nouvelle cascade de relance dans `BuildPowerShellScript` (`AutoUpdater.cs`) :

```
A) run_bepinex.bat existe à la racine ?         → on le lance (BepInEx s'injecte explicitement)
B) winhttp.dll + doorstop_config.ini présents ? → MiSide.exe direct OK (auto-injection Windows)
C) Fallback Steam URL                            → steam://rungameid/2527670 (respecte Launch Options)
D) Dernier recours : MiSide.exe brut             → avertissement utilisateur si BepInEx absent
```

### 2. Robustesse face aux ZIPs avec wrapper folder

**Bug observé** : le ZIP de la release v1.5.4 d'origine contenait par erreur un dossier wrapper `MiSideCoop_v1.5.4/` au top-level. Résultat : `Expand-Archive` extrayait dans `$GameRoot/MiSideCoop_v1.5.4/BepInEx/plugins/MiSideCoop.dll` au lieu de remplacer `$GameRoot/BepInEx/plugins/MiSideCoop.dll`. Le mod restait à la version précédente.

**Fix v1.5.5** : nouvelle étape **4b** dans le script PS1 — détection automatique des wrappers `MiSideCoop_v*/` après extraction et **aplatissement automatique** vers `$GameRoot`. Idempotent et compatible avec les releases passées et futures.

### 3. ZIP de release reconstruit en structure plate

À partir de v1.5.5, les ZIPs n'ont plus de wrapper folder — l'extraction directe place les fichiers aux bons endroits dès la première passe. Cohérent avec le format des releases v1.5.3 et antérieures.

---

## 📦 Fichiers modifiés

- `Plugin/PluginInfo.cs` — version 1.5.4 → 1.5.5
- `Plugin/Update/AutoUpdater.cs` — refonte du script PowerShell de relance (cascade A/B/C/D + aplatissement de wrapper)

---

## 🔄 Pour les utilisateurs actuels en v1.5.3 ou v1.5.4 cassé

Si après l'update vers v1.5.4 ton jeu a redémarré **sans BepInEx** (overlay du mod absent en haut à gauche) :

1. **Ferme MiSide** complètement.
2. **Relance MiSide via Steam normalement** (clic droit dans la bibliothèque → Jouer).
3. BepInEx va se ré-injecter → le mod réapparaît.
4. L'AutoUpdater va proposer **v1.5.5** → clique **Update Now**.
5. Cette fois l'update va passer correctement, et le jeu se relancera avec BepInEx en place automatiquement.

---

## 🚀 Installation manuelle

Identique aux releases précédentes :
1. Téléchargez `MiSideCoop.dll` depuis les assets de cette release.
2. Copiez-le dans `MiSide/BepInEx/plugins/`.
3. Lancez le jeu normalement.

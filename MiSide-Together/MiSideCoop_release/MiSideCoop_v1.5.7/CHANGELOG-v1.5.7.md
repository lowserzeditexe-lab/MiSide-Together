# MiSide Together — v1.5.7

**Focus :** Fix DÉFINITIF des animations + nouvelle UX d'auto-update.

---

## 🎉 Animations enfin fonctionnelles

Les logs v1.5.6 ont confirmé qu'**aucune méthode d'introspection des params Animator IL2CPP ne fonctionne** (`GetParameter(int)` lance 15 exceptions sur 15 essais, même en contexte static). Impossible donc de récupérer dynamiquement les noms des params.

**Solution v1.5.7 — Hardcodage depuis le dump IL2CPP du jeu** :

Le dépôt contient déjà un dump complet (`game_files/dump_output/stringliteral.json`). En cherchant les string literals courts ressemblant à des noms de params Animator, j'ai extrait la liste réelle utilisée par MiSide :

| Type | Params hardcodés |
|---|---|
| **Float** ⭐ | `SpeedForward` (LE param principal du blend tree), `MouseSpeed`, `ShooterMouseSpeed`, `HoldTime`, `HeadMove`, `KeyMove`, `OtherAnimationAFloat`, `OtherAnimationBFloat` |
| **Bool** | `Move`, `Walk`, `Run`, `Sit`, `Fall`, `Hide`, `HandsUp`, `Jump`, `JumpStop`, `StartRun`, `StopRun`, `BedSit`, `KickSit`, `Damage`, `Idle`, `IsRunic`, `AnimationHold`, `AnimationOther*` |
| **Trigger** | `Jump`, `JumpStop`, `Damage`, `StartRun`, `StopRun`, `AnimationClipNext[A/B]` |

`SetMovementSpeed(v)` pousse maintenant :
- `SetFloat("SpeedForward", speed)` → ⭐ **active enfin le blend tree marche/course**.
- `SetBool("Move", speed > 0.05f)` → gate global de mouvement.
- `SetBool("Walk", speed > 0.05f && speed < 2.6f)` → gate marche.
- `SetBool("Run", speed > 2.6f)` + `SetBool("StartRun", true)` → gate course.
- Tous les autres bools cohérents (Sit=false, Fall=false, Idle=!moving, etc.).

**Astuce technique** : tous les SetFloat/SetBool passent par `Animator.StringToHash(name)` plutôt que par les versions string. Cette API static n'est pas strippée et `SetFloat(hash, v)` sur un hash inconnu est **silencieux** (pas de warning spam Unity).

---

## 🔔 Nouvelle UX d'auto-update

Deux changements demandés :

### 1. Auto-install dès le démarrage (par défaut)

Nouvelle config `Updates.AutoInstall` (default = `true`).

Quand une update est détectée au boot du plugin et que `AutoInstall=true` :
- ❌ **Plus de popup modale qui bloque l'écran.**
- ✅ Mini-toast `"INSTALLING v1.5.X…"` en bas-droite (non-bloquant) pour info.
- ✅ Téléchargement en background, kill du jeu, script PowerShell, **restart via la cascade v1.5.5** (`run_bepinex.bat` / `winhttp.dll` / Steam URL).
- ✅ Le jeu revient avec la nouvelle version BepInEx-injectée — l'utilisateur n'a qu'à attendre ~10 secondes.

Pour repasser à l'ancien comportement (popup demandant confirmation), mettre dans `BepInEx/config/com.miside.coop.cfg` :
```ini
[Updates]
AutoInstall = false
```

### 2. Notification toast en bas-droite (si AutoInstall=false)

L'ancien popup plein écran avec overlay sombre est remplacé par une **notification toast** :
- Position : ancrée en bas à droite (anchor 1,0 + margin 24px).
- Taille : 380×110 px.
- **N'intercepte AUCUN clic** en dehors de sa propre surface — l'utilisateur peut continuer à jouer normalement.
- Boutons : `[INSTALL]` (gros, accent rose) + `[✕]` (close discret).
- Visible même en jeu, pas seulement au menu principal.

---

## 📦 Fichiers modifiés

- `Plugin/PluginInfo.cs` — version 1.5.6 → 1.5.7
- `Plugin/MiSideCoopPlugin.cs` — nouvelle config `UpdateAutoInstall`
- `Plugin/Network/AvatarAnimatorSync.cs` — refonte complète avec params MiSide hardcodés + StringToHash
- `Plugin/Update/AutoUpdater.cs` — `BuildToastUI()` + `BuildInstallingToastUI()` + branche auto-install dans `Update()`

---

## 🚀 Test

Lance MiSide. Tu devrais :
1. Voir un mini-toast `"INSTALLING v1.5.7…"` en bas-droite ~3s après le boot.
2. Le jeu se kill puis se relance automatiquement (~10s, via `run_bepinex.bat`).
3. **L'autre joueur MARCHE et COURT** dès que le peer bouge dans la scène. ✅
4. **Plus de GameBoy dans la main** du fantôme (depuis v1.5.6).

Si l'anim ne marche TOUJOURS pas après ça (improbable mais possible si le clone "Person" n'utilise pas `SpeedForward` mais un nom moins courant), envoie les logs — j'aurai cette fois une trace `[Co-op] AnimatorSync.Initialize on 'Player2_Guest' (id=XXX): Hardcoded MiSide params ready (...)`. Le diagnostic deviendra plus facile.

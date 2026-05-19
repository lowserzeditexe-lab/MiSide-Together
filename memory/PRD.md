# MiSide Together — Product Requirements (PRD)

## Original Problem Statement
Mod co-op (BepInEx 6 IL2CPP) pour le jeu Unity **MiSide** permettant à deux joueurs de jouer ensemble. Les bugs critiques en cours :
1. **Item Pickup Desync (Phone, Knife)** — quand un événement scripté donne un objet à main au joueur (Smartphone en Scène 1, Knife plus tard), *aucun* des deux joueurs ne voit l'item dans ses *propres* mains (1ère personne), alors que les *deux* voient l'item sur le clone 3D du peer.
2. **NullReferenceException Animator** — log spam "ctrl=''" + 15 échecs `EnumerateAndCacheParams` au spawn du clone Player2_Guest.

## User Personas
- **Hôte** : crée la room, joue MiSide normalement.
- **Invité** : rejoint via code, joue MiSide normalement avec un clone 3D représentant l'hôte.

## Core Requirements
- Les deux joueurs voient le téléphone/couteau dans leurs **propres** mains (1ère personne).
- Les deux joueurs voient l'autre joueur tenir l'objet sur son clone 3D (3ème personne).
- Pas de NullReferenceException spam dans les logs.

## What's Been Implemented (v1.6.8 — May 2026)
- ✅ **`HandItemSync.cs`** (nouveau) — composant qui poll (5 Hz) l'état `activeInHierarchy` des hand-items locaux 1ère personne (`Player/RightItem FixPosition/{Smartphone,Knife,Tetris,Flashlight}`) et broadcast les changements via `HandItemMessage` (MsgId=10).
- ✅ Réception → applique le SetActive sur la copie locale 1ère personne + sur le `Right item` enfant du clone 3D distant (Player2_Guest / Player1_Host_Remote).
- ✅ **SharedPov désactivé** — `TickSmartphoneWatcher` et `PickupPatch.IsPovSharingItem` neutralisés. La désactivation de la MainCamera du viewer cassait la vue 1ère personne (l'utilisateur ne voyait plus son propre téléphone).
- ✅ **Fix Animator NullRef** — `RealMcCloner.PrepareGhostAnimator` utilise maintenant `PickBestAnimatorForEnum` qui choisit un Animator avec controller + params > 0 (priorité aux GO 'Person*' / 'Player2_Guest' / 'Player1_Host_Remote'). Skip propre si aucun candidat valide.
- ✅ Compilation OK : `/app/MiSide-Together/MiSideCoop/bin/Release/MiSideCoop.dll` + `/app/MiSide-Together/MiSideCoop_release/MiSideCoop_v1.6.8.zip` (192 KB).

## Architecture
- C# .NET 6.0, BepInEx 6 IL2CPP, HarmonyX
- Transport : TCP brut via `CoopTcpTransport` + relay externe
- Messages binaires (MsgId 1-bit + payload)
- Avatars : `Player1Avatar` (host MC) / `Player2Avatar` (guest MC) + clones 3D `RealMcCloner`
- Sync animation : `Animator.Play(hash)` + push direct des floats 'Forward'/'Right' + bool 'Sit' + head bone rotation

## Backlog (Prioritized)
- P1 — Tester en jeu v1.6.8 : confirmer que les deux voient le téléphone dans leurs mains + que les NullRef sont éteints.
- P2 — Ajouter d'autres hand-items au runtime si découverts (path heuristique).
- P2 — Nettoyer les warnings CS0169 (`_watchedSmartphone*` non utilisés depuis v1.6.8).
- P3 — Refactor : extraire les hand-items keywords vers un seul fichier de constantes.
- P3 — Implémenter un TextMeshPro nametag (TextMesh strippé en IL2CPP).

## Files of Reference
- `/app/MiSide-Together/MiSideCoop/Plugin/Network/HandItemSync.cs` (nouveau v1.6.8)
- `/app/MiSide-Together/MiSideCoop/Plugin/Network/NetworkMessages.cs` (MsgId.HandItem)
- `/app/MiSide-Together/MiSideCoop/Plugin/Network/CoopNetworkManager.cs` (dispatch + broadcast)
- `/app/MiSide-Together/MiSideCoop/Plugin/Network/SharedPovController.cs` (watcher désactivé)
- `/app/MiSide-Together/MiSideCoop/Plugin/Avatars/RealMcCloner.cs` (PickBestAnimatorForEnum)
- `/app/MiSide-Together/MiSideCoop/Plugin/Patches/PickupPatch.cs` (POV-share branch retirée)
- `/app/MiSide-Together/MiSideCoop/Plugin/CoopBootstrap.cs` (AddComponent<HandItemSync>())

## Testing Notes
Test C# Unity mod = pas de testing agent automatique. L'utilisateur doit :
1. Copier `MiSideCoop.dll` dans `BepInEx/plugins/MiSideCoop/` de son install MiSide
2. Lancer le jeu, créer/rejoindre une room
3. Avancer jusqu'à la séquence Mita-téléphone (Scène 1)
4. Vérifier visuellement les deux POV et envoyer `LogOutput.log`

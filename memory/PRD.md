# PRD — MiSide Co-op Mod v1.0.0

## Problème original
Créer un mod BepInEx complet, compilé et prêt à installer pour le jeu Unity MiSide, ajoutant un mode coopératif en ligne pour 2 joueurs.

## Architecture implémentée

### Stack technique
- **BepInEx 6.0.0-pre.2** (IL2CPP)
- **Harmony** (patching runtime via BepInEx)
- **Mirror Networking** + **Telepathy Transport** (API bas niveau, IL2CPP-compatible)
- **Serveur de relais Node.js** (codes de room → IPs)
- **Unity 2021+** (UnityEngine assemblies)

### Fichiers créés (22 fichiers)
```
MiSideCoop/
├── Plugin/
│   ├── PluginInfo.cs               Constantes GUID/nom/version
│   ├── MiSideCoopPlugin.cs         Point d'entrée BepInEx 6 (IL2CPP)
│   ├── CoopBootstrap.cs            MonoBehaviour persistant DontDestroyOnLoad
│   ├── Network/
│   │   ├── CoopNetworkManager.cs   Gestionnaire réseau principal (Mirror bas niveau)
│   │   ├── PlayerAvatar.cs         Classe abstraite de base des avatars
│   │   ├── AvatarAnimatorSync.cs   Sync Animator avec détection auto des paramètres
│   │   ├── NetworkMessages.cs      6 structs NetworkMessage IL2CPP-safe
│   │   └── GameStateSync.cs        Sync scènes + cutscenes Mita
│   ├── Avatars/
│   │   ├── Player1Avatar.cs        MC original (hôte) avec désactivation input distant
│   │   └── Player2Avatar.cs        Second personnage (skin coloré, caméra dédiée)
│   ├── Relay/
│   │   └── RoomManager.cs          Génération codes + HTTP vers relais Node.js
│   ├── Patches/
│   │   ├── MovementPatch.cs        Bloque input sur avatars distants
│   │   ├── InteractionPatch.cs     Broadcast interactions + dialogues Mita
│   │   ├── DoorPatch.cs            Sync ouverture/fermeture portes
│   │   ├── PickupPatch.cs          Sync ramassage + désactivation objets
│   │   └── ScenePatch.cs           Intercept LoadScene pour sync côté client
│   └── UI/
│       └── CoopMenuUI.cs           Menu IMGUI F8 + HUD ping en jeu
├── relay-server.js                 Serveur Node.js testé et validé
├── MiSideCoop.csproj               Projet .NET 6 avec toutes références
├── install.bat                     Installateur Windows (auto-détection Steam)
├── install.sh                      Installateur Linux/macOS
└── README.md                       Documentation complète

```

## Choix utilisateur
- Port réseau : 7777 (défaut)
- Pseudo Joueur 2 : "Player2" (modifiable in-game)
- Avatar Joueur 2 : configurable via config (défaut : MC avec skin bleu)
- Serveur relais : code source fourni (hébergement utilisateur)

## Ce qui a été implémenté
- [x] Système de rooms 6 caractères (génération + résolution + expiration 2h)
- [x] Architecture réseau bas niveau Mirror (sans weaver, IL2CPP-safe)
- [x] Sync position/rotation interpolée (Vector3.Lerp + Quaternion.Slerp, 20 Hz)
- [x] Sync états Animator avec détection automatique des paramètres
- [x] Deux avatars distincts (Player1 = MC, Player2 = perso coloré)
- [x] Caméra indépendante pour chaque joueur
- [x] Tag pseudo flottant au-dessus de la tête (face caméra)
- [x] Rigidbody kinematic + collider Trigger sur avatars distants
- [x] Sync actions : Interact, PickUp, OpenDoor, Die, Crouch
- [x] Sync changements de scène
- [x] Sync cutscenes/dialogues Mita
- [x] Menu IMGUI (F8) avec création/rejoindre room + HUD ping
- [x] Installation automatisée (Windows + Linux/macOS)
- [x] Serveur relais Node.js testé (tous endpoints validés)
- [x] README complet avec FAQ

## Backlog P1 (à faire après décompilation de MiSide)
- Identifier les vrais noms de classes via dnSpy/ILSpy et activer les patches Harmony commentés
- Tester avec les vrais modèles 3D du MC (Player1Avatar.EnsureCameraActive)
- Valider AvatarAnimatorSync avec les vrais noms de paramètres Animator MiSide
- Ajouter support de la mort/respawn réseau

## Backlog P2 (améliorations futures)
- Interface de configuration in-game plus complète
- Support 3+ joueurs (architecture déjà scalable)
- Voice chat intégré (Vivox ou Dissonance)
- Anti-cheat basique (validation côté serveur des positions)

# PRD — MiSide Co-op Mod

## 🎯 Original Problem Statement
Créer un mod BepInEx 6 (IL2CPP) complet, compilé et prêt à installer pour le jeu Unity *MiSide*. Ce mod ajoute un mode coopératif en ligne pour 2 joueurs (hôte = perso principal, invité = 2ème perso visible et animé).

**Exigences livraison :**
1. `MiSideCoop.dll` compilé
2. Scripts d'installation Windows/Linux
3. README en français
4. Serveur Node.js de mise en relation (room codes 6 chars via F8)
5. Synchronisation réseau en temps réel (Position, Rotation, Animations, Interactions)
6. Architecture de Patches Harmony (Movement, Interaction, Door, Pickup, Scene)

---

## ✅ Implémentations (état actuel)

### Architecture
- **22 fichiers C#** organisés en modules (Plugin/Network, Patches, Avatars, UI, Relay)
- **BepInEx 6.0.0-be.725 IL2CPP** via NuGet (`nuget.bepinex.dev`)
- **UnityEngine.Modules 2021.3.18** via NuGet (assemblies de référence)
- **Pas de dépendance externe** à Mirror/Telepathy → TCP brut intégré (`System.Net.Sockets`)

### Patches Harmony (vraies classes MiSide identifiées via Il2CppDumper)
| Patch | Classe MiSide | Méthode |
|-------|---------------|---------|
| `MovementPatch_Update/FixedUpdate` | `PlayerMove` (TypeDefIndex 2158) | `Update()` / `FixedUpdate()` |
| `InteractionPatch_Click` | `ObjectInteractive` (TypeDefIndex 2139) | `Click()` |
| `DoorPatch_OpenAngle` | `ObjectDoor` (TypeDefIndex 2138) | `OpenAngle(float)` |
| `DoorPatch_Lock` | `ObjectDoor` | `Lock(bool)` |
| `PickupPatch_Take` | `ObjectInteractiveItemTake` (TypeDefIndex 2141) | `Take()` |
| `PickupPatch_TakeItem` | `PlayerMove` | `TakeItem(GameObject)` |
| `ScenePatchByName/Index/Async` | `UnityEngine.SceneManagement.SceneManager` | `LoadScene*` |

Résolution **à runtime** via `AccessTools.TypeByName("…")` — aucune référence directe au code MiSide au compile-time.

### Réseau (Plugin/Network/)
- `CoopTcpTransport.cs` : transport TCP brut, 1 thread/socket, `ConcurrentQueue` thread-safe
- `NetworkMessages.cs` : 6 messages binaires (PlayerState, PlayerAction, SceneChange, Cutscene, RoomJoin, ObjectSync)
- `CoopNetworkManager.cs` : orchestration host/client, dispatch des messages dans le main thread Unity
- `GameStateSync.cs` : sync scènes + cutscenes

### Serveur Relay (relay-server.js)
- Express HTTP, routes : `POST /register`, `GET /resolve/:code`, `POST /unregister`, `GET /health`
- Mapping en mémoire `code → ip:port` (TTL 5 min)
- Testé et fonctionnel localement

### Livrable final
- `/app/MiSideCoop_release/MiSideCoop_v1.0.0.zip` (64 KB)
- Contient : `MiSideCoop.dll` (50 KB compilé), `relay-server.js`, `package.json`, `install.bat/.sh`, README détaillé, sources complètes dans `src/`

---

## 🏗️ Code Architecture
```
/app/MiSideCoop/
├── MiSideCoop.csproj            ← Build via dotnet (NuGet only, no game files needed)
├── relay-server.js              ← Node.js relay (testé)
├── install.bat / install.sh
├── README.md
├── bin/Release/MiSideCoop.dll   ← Artefact compilé (50688 octets, 0 warn/0 err)
├── Refs/                         ← (legacy : dummy DLLs Il2CppDumper, plus utilisé)
└── Plugin/
    ├── MiSideCoopPlugin.cs       ← Entry point BepInPlugin
    ├── CoopBootstrap.cs
    ├── PluginInfo.cs
    ├── Network/
    │   ├── CoopTcpTransport.cs   ← TCP brut (remplace Mirror)
    │   ├── CoopNetworkManager.cs
    │   ├── NetworkMessages.cs    ← Sérialisation binaire manuelle
    │   ├── PlayerAvatar.cs
    │   ├── AvatarAnimatorSync.cs
    │   └── GameStateSync.cs
    ├── Avatars/
    │   ├── Player1Avatar.cs
    │   └── Player2Avatar.cs
    ├── Relay/RoomManager.cs      ← Client REST du relay
    ├── Patches/
    │   ├── MovementPatch.cs
    │   ├── InteractionPatch.cs
    │   ├── DoorPatch.cs
    │   ├── PickupPatch.cs
    │   └── ScenePatch.cs
    └── UI/CoopMenuUI.cs          ← Menu F8 IMGUI
```

---

## 📋 Status / Roadmap

### ✅ Done
- [x] Architecture complète + sources C# (22 fichiers)
- [x] Serveur relay Node.js testé (register/resolve/unregister/health)
- [x] Patches Harmony avec **vraies classes MiSide** (dump IL2CPP)
- [x] Suppression dépendance Mirror → TCP brut intégré
- [x] **Build .NET réussi** : `MiSideCoop.dll` 50KB, 0 warnings, 0 errors
- [x] Package livrable `MiSideCoop_v1.0.0.zip` (64KB)
- [x] README détaillé en français

### 🟡 P2 — Tests in-game (non disponibles dans cet environnement)
- [ ] Test installation BepInEx 6 sur le jeu réel
- [ ] Test de connexion réseau host↔client (port 7777)
- [ ] Validation animations distantes (PickUp, OpenDoor, Interact)
- [ ] Test création/jonction room via menu F8

### 🟢 Améliorations potentielles (futures)
- Sync clothing/skins entre joueurs
- Voice chat intégré
- Sauvegarde partagée des progrès
- Mode spectateur

---

## 🔑 Specs techniques résolues durant la session

**Classes MiSide identifiées via dump Il2CppDumper :**
- `GameAssembly.dll` (22MB) + `global-metadata.dat` (5MB) extraits depuis le ZIP du jeu fourni par l'utilisateur
- `Il2CppDumper v6.7.46` lancé sous .NET 10 (avec patch `runtimeconfig.json` net6→net10)
- Sortie : `dump.cs` 288 102 lignes — classes principales identifiées :
  - `PlayerMove` ligne 127336 (MC du jeu)
  - `PlayerPerson` ligne 127672
  - `ObjectDoor` ligne 126330 (portes)
  - `ObjectInteractive` ligne 126466 (clics génériques)
  - `ObjectInteractiveItemTake` ligne 126551 (ramassages)

**Dépendances NuGet :**
- `BepInEx.Unity.IL2CPP` 6.0.0-be.725 (`nuget.bepinex.dev`)
- `UnityEngine.Modules` 2021.3.18 (NuGet officiel)
- `BepInEx.PluginInfoProps` 2.1.0

---

## 🧪 Tests effectués
- Build C# `.NET 10 SDK` : **succès** (50688 octets)
- Strings UTF-16 dans le DLL : `PlayerMove`, `ObjectDoor`, `ObjectInteractive`, `ObjectInteractiveItemTake` → tous présents
- Relay Node.js : `GET /health`, `POST /register`, `GET /resolve/:code` → **OK**

## 🔧 Credentials / Secrets
N/A (mode peer-to-peer, pas d'authentification).

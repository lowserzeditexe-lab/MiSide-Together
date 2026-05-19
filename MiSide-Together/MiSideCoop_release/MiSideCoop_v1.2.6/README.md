# MiSide Co-op Mod v1.0.0

Mod BepInEx 6 (IL2CPP) ajoutant un **mode coopératif en ligne pour 2 joueurs** au jeu Unity *MiSide*.

## ✨ Concept

- **Joueur 1 (hôte)** : incarne le MC original (Mita's intro / Mita Online), conserve tous ses scripts de contrôle.
- **Joueur 2 (invité)** : incarne un second personnage avec un avatar coloré distinct.
- Les deux joueurs coexistent dans la même scène, se voient en temps réel et leurs actions (déplacements, animations, interactions, ouvertures de porte, ramassages) sont mutuellement visibles.

---

## 📦 Contenu du package

```
MiSideCoop_v1.0.0/
├── MiSideCoop.dll          ← le plugin BepInEx compilé
├── relay-server.js          ← serveur Node.js de mise en relation
├── install.bat              ← script d'installation Windows
├── install.sh               ← script d'installation Linux/macOS
└── README.md                ← ce fichier
```

---

## 🛠️ Prérequis

| Composant | Version | Source |
|-----------|---------|--------|
| MiSide | Dernière version Steam | Steam |
| BepInEx | 6.0.0-be.725+ (IL2CPP, Windows x64) | [builds.bepinex.dev](https://builds.bepinex.dev/projects/bepinex_be) |
| .NET SDK | 6.0+ (pour recompiler) | [microsoft.com/dotnet](https://dotnet.microsoft.com/download) |
| Node.js | 16+ (pour le serveur relais) | [nodejs.org](https://nodejs.org) |

> **Pas de dépendance externe à Mirror Networking** : le mod utilise un transport TCP brut intégré (System.Net.Sockets).

---

## 🚀 Installation rapide

### Windows
```batch
install.bat
```

### Linux / macOS (pour serveurs Steam Proton)
```bash
chmod +x install.sh && ./install.sh
```

Les scripts :
1. Détectent MiSide via le registre Steam ou les chemins par défaut
2. Installent BepInEx 6 IL2CPP automatiquement si absent
3. Copient `MiSideCoop.dll` dans `BepInEx/plugins/MiSideCoop/`

### Installation manuelle
1. Téléchargez **BepInEx 6 Unity.IL2CPP win-x64** : <https://builds.bepinex.dev/projects/bepinex_be>
2. Extrayez le contenu dans le dossier MiSide (à côté de `MiSideFull.exe`)
3. Lancez **une fois** le jeu (BepInEx génère les `BepInEx/interop/` à partir des classes IL2CPP)
4. Quittez, puis copiez `MiSideCoop.dll` dans `BepInEx/plugins/`
5. Relancez le jeu

---

## 🌐 Démarrer le serveur relay

Le serveur Node.js maintient un mapping `code de room → adresse IP:port` pour permettre la mise en relation entre joueurs sans port forwarding du côté hôte.

```bash
# Sur n'importe quelle machine accessible publiquement
node relay-server.js
# Par défaut : port 3000
# Personnalisé : PORT=8080 node relay-server.js
```

API REST exposée :
- `POST /register` `{ code, ip, port }` — l'hôte s'enregistre
- `GET  /resolve/:code` — l'invité résout le code
- `POST /unregister` `{ code }` — l'hôte ferme la room
- `GET  /health` — état du serveur

---

## 🎮 Utilisation in-game

1. **Hôte** :
   - Lance MiSide
   - Appuie sur **F8** pour ouvrir le menu Co-op
   - Clique sur **"Créer une room"** → un code à 6 caractères est généré
   - Communique ce code à son invité

2. **Invité** :
   - Lance MiSide
   - Appuie sur **F8**
   - Clique sur **"Rejoindre une room"**
   - Saisit le code reçu → connexion automatique

3. Le second avatar apparaît dans la scène. F8 ferme/rouvre le menu sans rompre la connexion.

---

## ⚙️ Configuration (BepInEx/config/com.miside.coop.cfg)

Généré au premier lancement après chargement du mod :

```ini
[Network]
RelayServerUrl = http://localhost:3000
Port = 7777                ## Port TCP de l'hôte ; ouvert dans le pare-feu si NAT

[Avatar]
Player2SkinColor = Blue    ## Red, Blue, Green, Yellow, Purple, Orange, White

[General]
PlayerName = Player2       ## Nom affiché au-dessus de l'avatar distant
```

---

## 🏗️ Compilation depuis les sources

```bash
cd MiSideCoop
dotnet restore
dotnet build MiSideCoop.csproj -c Release
```

Le DLL sort dans `bin/Release/MiSideCoop.dll`.

Les packages NuGet utilisés :
- `BepInEx.Unity.IL2CPP` 6.0.0-be.725 (depuis nuget.bepinex.dev)
- `UnityEngine.Modules` 2021.3.18 (NuGet officiel — assemblies de référence Unity)
- `BepInEx.PluginInfoProps` 2.1.0

---

## 🔬 Architecture technique

### Patches Harmony (Plugin/Patches/)

Toutes les classes MiSide sont résolues à **runtime** via `AccessTools.TypeByName(...)`. Aucune référence directe au code du jeu au compile.

| Patch                     | Classe MiSide ciblée           | Méthode             | Effet |
|---------------------------|--------------------------------|---------------------|-------|
| `MovementPatch_Update`    | `PlayerMove`                   | `Update()`          | Bloque les inputs sur les avatars distants |
| `MovementPatch_FixedUpdate`| `PlayerMove`                  | `FixedUpdate()`     | Idem (physique) |
| `InteractionPatch_Click`  | `ObjectInteractive`            | `Click()`           | Broadcast `Interact` |
| `DoorPatch_OpenAngle`     | `ObjectDoor`                   | `OpenAngle(float)`  | Sync rotation porte |
| `DoorPatch_Lock`          | `ObjectDoor`                   | `Lock(bool)`        | Sync verrouillage |
| `PickupPatch_Take`        | `ObjectInteractiveItemTake`    | `Take()`            | Désactive l'objet partout |
| `PickupPatch_TakeItem`    | `PlayerMove`                   | `TakeItem(GameObject)` | Trigger anim PickUp |
| `ScenePatchByName/Index/Async` | `UnityEngine.SceneManagement.SceneManager` | `LoadScene*` | Sync changements de scène |

### Transport réseau (Plugin/Network/CoopTcpTransport.cs)

- TCP brut via `System.Net.Sockets.TcpListener` / `TcpClient`
- Trames : `[int32 length][byte msgId][payload …]`
- Sérialisation manuelle via `BinaryReader`/`BinaryWriter`
- 1 thread de lecture par socket, dispatch côté Unity main thread via `ConcurrentQueue`

### Messages applicatifs (Plugin/Network/NetworkMessages.cs)

| MsgId | Message | Contenu |
|-------|---------|---------|
| 1 | `PlayerStateMessage` | Pos+Rot+Speed+AnimState (20 Hz) |
| 2 | `PlayerActionMessage`| ActionType + Target |
| 3 | `SceneChangeMessage` | Scène + SpawnPoint |
| 4 | `CutsceneMessage`    | Id + Start/End |
| 5 | `RoomJoinMessage`    | Identification |
| 6 | `ObjectSyncMessage`  | État on/off + position |

---

## 🐛 Dépannage

**Le mod ne se charge pas** → Vérifiez `BepInEx/LogOutput.log` pour les exceptions de chargement. Cause fréquente : BepInEx 6 IL2CPP n'a pas généré les assemblies interop (lancez le jeu une fois avant d'installer le mod).

**Port 7777 fermé** → L'hôte doit ouvrir le port TCP 7777 dans son pare-feu Windows + router (port forwarding) si ses joueurs sont distants. Pour LAN uniquement, le pare-feu suffit.

**Le second avatar n'apparaît pas** → Vérifiez que `PlayerMove` est bien trouvé : recherchez `[Co-op] Avatar` dans les logs. Sinon, le MC du jeu utilise peut-être un autre composant — patchez via dnSpy le dump `dump.cs`.

**Code de room invalide** → Le relay ne garde une room que 5 min sans heartbeat. L'hôte doit re-créer la room.

---

## 📜 Crédits / Licence

Mod indépendant non affilié à AIHASTO (créateurs de MiSide).
Licence MIT.

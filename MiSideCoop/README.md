# MiSide Co-op Mod v1.0.0

Mod BepInEx 6 (IL2CPP) ajoutant un **mode coopératif en ligne pour 2 joueurs** au jeu MiSide.

## Concept

- **Joueur 1 (hôte)** : incarne le MC original, conserve tous ses scripts de contrôle.
- **Joueur 2 (invité)** : incarne un second personnage distinct avec son propre avatar coloré.
- Les deux joueurs coexistent dans la même scène, se voient en temps réel et leurs actions sont mutuellement visibles.

---

## Prérequis

| Composant | Version | Source |
|-----------|---------|--------|
| MiSide | Dernière version Steam | Steam |
| BepInEx | 6.0.0-pre.2 (IL2CPP) | [Releases](https://github.com/BepInEx/BepInEx/releases) |
| Mirror Networking | ≥ 66.0 | [Releases](https://github.com/MirrorNetworking/Mirror/releases) |
| .NET SDK | 6.0+ | [microsoft.com/dotnet](https://dotnet.microsoft.com/download) |
| Node.js | 16+ | [nodejs.org](https://nodejs.org) (pour le serveur relais) |

---

## Installation rapide

### Windows
```batch
install.bat
```

### Linux / macOS
```bash
chmod +x install.sh && ./install.sh
```

Les scripts :
1. Détectent MiSide via le registre Steam ou les chemins par défaut
2. Installent BepInEx 6 automatiquement si absent
3. Copient `MiSideCoop.dll`, `Mirror.dll`, `Telepathy.dll` dans `BepInEx/plugins/MiSideCoop/`
4. Copient `relay-server.js` dans le dossier du jeu

---

## Compilation (si vous n'avez pas le DLL précompilé)

### Étape 1 — Obtenir Mirror.dll et Telepathy.dll

Téléchargez la dernière release de Mirror :
https://github.com/MirrorNetworking/Mirror/releases

Extrayez `Mirror.dll` et `Telepathy.dll`, copiez-les dans :
```
BepInEx/plugins/MiSideCoop/Mirror.dll
BepInEx/plugins/MiSideCoop/Telepathy.dll
```

### Étape 2 — Compiler le mod

```bash
# Depuis le dossier MiSideCoop/
dotnet build MiSideCoop.csproj --configuration Release

# Ou avec un chemin de jeu personnalisé :
dotnet build MiSideCoop.csproj -c Release -p:GamePath="D:\Games\MiSide"
```

Le DLL compilé se trouve dans `bin/Release/MiSideCoop.dll`.

### Étape 3 — Déployer

Relancez `install.bat` / `install.sh` ou copiez manuellement `MiSideCoop.dll` vers :
```
[MiSide]/BepInEx/plugins/MiSideCoop/MiSideCoop.dll
```

---

## Utilisation

### Démarrer le serveur de relais (recommandé)

Le serveur de relais permet aux joueurs de se connecter via un code à 6 caractères sans partager leur IP. Hébergez-le sur un VPS ou une machine accessible publiquement.

```bash
node relay-server.js
# Port par défaut : 3000
# Modifier : PORT=8080 node relay-server.js
```

Configurez ensuite l'URL du relais dans :
```
BepInEx/config/com.miside.coop.cfg
```
```ini
[Network]
RelayServerUrl = http://votre-serveur.com:3000
Port = 7777
```

### Créer une session (Hôte)

1. Lancez MiSide
2. Appuyez sur **F8** → cliquez **"Créer une room"**
3. Un code à 6 caractères s'affiche (ex : `XK4F2A`)
4. Cliquez **"Copier le code"** et envoyez-le à votre ami
5. Attendez la connexion — le menu affichera le pseudo de l'invité

> L'hôte doit ouvrir le **port TCP 7777** dans son pare-feu.
> `install.bat` le fait automatiquement sous Windows.

### Rejoindre une session (Invité)

1. Lancez MiSide
2. Appuyez sur **F8** → cliquez **"Rejoindre une room"**
3. Entrez le code à 6 caractères de l'hôte
4. Cliquez **"Rejoindre"**

---

## Configuration complète

Fichier : `BepInEx/config/com.miside.coop.cfg`

```ini
[General]
# Votre pseudo affiché au-dessus de votre avatar
PlayerName = Player2

[Network]
# URL du serveur de relais Node.js
RelayServerUrl = http://localhost:3000
# Port TCP Mirror/Telepathy
Port = 7777

[Avatar]
# Couleur du skin Joueur 2 : Red, Blue, Green, Yellow, Purple, Orange, White
Player2SkinColor = Blue
```

---

## Fonctionnalités synchronisées

| Action | Synchronisée |
|--------|:------------:|
| Marcher / Courir | ✅ |
| S'arrêter (Idle) | ✅ |
| Interagir avec un objet | ✅ |
| Ouvrir une porte | ✅ |
| Ramasser un objet | ✅ |
| Dialogue avec Mita (cutscene) | ✅ |
| Mourir / être attrapé | ✅ |
| Changement de scène | ✅ |
| Position / Rotation (interpolée) | ✅ |
| Pseudo flottant au-dessus de la tête | ✅ |

---

## Architecture technique

```
MiSide Co-op
├── CoopBootstrap          MonoBehaviour persistant (DontDestroyOnLoad)
├── CoopNetworkManager     API Mirror bas niveau (messages manuels, IL2CPP-safe)
│   ├── PlayerStateMessage   Sync position/rotation/animation (20 Hz)
│   ├── PlayerActionMessage  Actions ponctuelles (interact, pickup…)
│   ├── SceneChangeMessage   Sync changement de scène
│   ├── CutsceneMessage      Sync cutscenes Mita
│   └── ObjectSyncMessage    Sync état objets (portes, items)
├── Player1Avatar          Avatar MC (hôte)
├── Player2Avatar          Avatar invité (skin coloré, caméra dédiée)
├── AvatarAnimatorSync     Applique les états d'animation réseau sur l'Animator
├── GameStateSync          Broadcast scènes + cutscenes côté hôte
├── RoomManager            Génération codes + communication HTTP relais
├── CoopMenuUI             Interface IMGUI (F8) + HUD ping en jeu
└── Patches Harmony
    ├── MovementPatch      Bloque l'input sur avatars distants
    ├── InteractionPatch   Broadcast interactions / dialogues Mita
    ├── DoorPatch          Broadcast ouverture/fermeture portes
    ├── PickupPatch        Broadcast ramassage + désactivation objets
    └── ScenePatch         Intercepte LoadScene pour sync côté client
```

---

## Intégration des patches Harmony

Les patches sont conçus pour être facilement adaptés une fois les vraies classes de MiSide identifiées via **dnSpy** ou **ILSpy** (décompilateur).

Exemple pour `InteractionPatch` — une fois `PlayerInteraction.Interact()` identifiée :

```csharp
[HarmonyPatch(typeof(PlayerInteraction), "Interact")]
[HarmonyPostfix]
public static void Postfix(MonoBehaviour __instance, GameObject target)
    => InteractionPatch.NotifyInteraction(__instance.gameObject, target);
```

Même principe pour `DoorPatch.NotifyDoorStateChange()` et `PickupPatch.NotifyPickup()`.

---

## FAQ

**Q : Le jeu plante au démarrage après l'installation.**
R : Vérifiez que `Mirror.dll` et `Telepathy.dll` sont bien dans `BepInEx/plugins/MiSideCoop/`. Consultez `BepInEx/LogOutput.log` pour les détails.

**Q : L'invité ne voit pas le MC bouger.**
R : Vérifiez que le port 7777 est ouvert côté hôte. Confirmez que la même version du mod est installée des deux côtés.

**Q : Le code de room indique "introuvable".**
R : Le serveur de relais n'est pas accessible. Vérifiez que `relay-server.js` tourne et que l'URL dans le fichier de config est correcte.

**Q : Les animations ne jouent pas sur l'avatar distant.**
R : Les noms de paramètres Animator de MiSide ont peut-être des noms différents. Consultez `BepInEx/LogOutput.log` pour voir lesquels sont détectés. Adaptez `AvatarAnimatorSync.cs` si nécessaire.

**Q : Comment changer la couleur de l'avatar Joueur 2 ?**
R : Modifiez `Player2SkinColor` dans `com.miside.coop.cfg`. Valeurs : Red, Blue, Green, Yellow, Purple, Orange, White.

**Q : Le mod est-il compatible avec les futures mises à jour de MiSide ?**
R : Probablement, tant que les méthodes de base Unity (SceneManager, Animator) ne changent pas radicalement. Les patches Harmony peuvent nécessiter une mise à jour si les classes internes du jeu changent.

---

## Serveur de relais — déploiement

```bash
# Sur votre serveur (VPS, machine dédiée…)
git clone / copiez relay-server.js
node relay-server.js

# Avec PM2 pour la persistance :
npm install -g pm2
pm2 start relay-server.js --name miside-relay
pm2 startup && pm2 save

# Vérification :
curl http://votre-serveur:3000/health
# → {"status":"ok","rooms":0,"uptime":…}
```

---

## Structure du projet

```
MiSideCoop/
├── Plugin/
│   ├── MiSideCoopPlugin.cs       Point d'entrée BepInEx
│   ├── PluginInfo.cs             Constantes (GUID, nom, version)
│   ├── CoopBootstrap.cs          MonoBehaviour de démarrage
│   ├── Network/
│   │   ├── CoopNetworkManager.cs Gestionnaire réseau principal
│   │   ├── PlayerAvatar.cs       Classe de base des avatars
│   │   ├── AvatarAnimatorSync.cs Synchronisation Animator
│   │   ├── NetworkMessages.cs    Structures de messages Mirror
│   │   └── GameStateSync.cs      Sync scènes + cutscenes
│   ├── Avatars/
│   │   ├── Player1Avatar.cs      Avatar hôte (MC)
│   │   └── Player2Avatar.cs      Avatar invité (skin coloré)
│   ├── Relay/
│   │   └── RoomManager.cs        Gestion codes de room
│   ├── Patches/
│   │   ├── MovementPatch.cs      Patch déplacement
│   │   ├── InteractionPatch.cs   Patch interactions
│   │   ├── DoorPatch.cs          Patch portes
│   │   ├── PickupPatch.cs        Patch ramassage
│   │   └── ScenePatch.cs         Patch changement de scène
│   └── UI/
│       └── CoopMenuUI.cs         Menu F8 + HUD
├── relay-server.js               Serveur Node.js de relais
├── MiSideCoop.csproj             Projet .NET 6
├── install.bat                   Installateur Windows
├── install.sh                    Installateur Linux/macOS
└── README.md                     Ce fichier
```

---

## Licence

MIT License — Utilisation libre. Pas affilié à AIHASTO (développeur de MiSide).

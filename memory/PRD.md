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
- `/app/MiSideCoop_release/MiSideCoop_v1.0.3.zip` (96 KB) — **dernière version (uGUI)**
- Versions précédentes (v1.0.0 → v1.0.2) conservées pour historique
- Contient : `MiSideCoop.dll` compilé, `relay-server.js`, `package.json`, `install.bat/.sh`, README détaillé, sources complètes dans `src/`
- **Script de build automatisé** : `/app/MiSideCoop/build_release.sh` — incrémente la version automatiquement et produit `MiSideCoop_v<X.Y.Z>.zip`

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
- [x] Architecture complète + sources C# (22 fichiers → 23 avec BootstrapRecovery)
- [x] Serveur relay Node.js testé (register/resolve/unregister/health)
- [x] Patches Harmony avec **vraies classes MiSide** (dump IL2CPP)
- [x] Suppression dépendance Mirror → TCP brut intégré
- [x] **Build .NET réussi** : `MiSideCoop.dll` 52KB, 0 warnings, 0 errors
- [x] Package livrable `MiSideCoop_v1.0.0.zip` mis à jour (88KB)
- [x] README détaillé en français
- [x] **Fix DontDestroyOnLoad IL2CPP** : déplacé dans `CoopBootstrap.Awake()` avec `SetParent(null)`
- [x] **Auto-récupération bootstrap** : `BootstrapRecovery` + `ScenePatch Postfix`
- [x] **Menu auto-ouvert au lancement** : `_state = MenuState.Main` (ouvert par défaut, F8 pour fermer/rouvrir)

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
- Build C# `.NET 10 SDK` : **succès** (v1.0.1 — 0 warning, 0 error)
- Strings UTF-16 dans le DLL : `PlayerMove`, `ObjectDoor`, `ObjectInteractive`, `ObjectInteractiveItemTake` → tous présents
- Relay Node.js : `GET /health`, `POST /register`, `GET /resolve/:code` → **OK**

## 📝 Historique des fixes IL2CPP (v1.0.x / v1.1.x)
- **v1.0.1** — DontDestroyOnLoad : bootstrap déplacé dans `Internal_SceneLoaded` + `BootstrapRecovery` failsafe
- **v1.0.1** — `GUI.WindowFunction..ctor` : remplacé `GUI.Window` par `GUI.Box` + `GUILayout.BeginArea`
- **v1.0.1** — `RectOffset..ctor(int,int,int,int)` : suppression du ctor multi-args ; fallback `GUI.skin.*`
- **v1.0.2** — `GUILayout.BeginArea` strippé : migration tous les écrans vers `GUI.*` absolu (sans GUILayout)
- **v1.0.3** — **Migration vers uGUI** (Canvas + GameObjects) suite à `GUIStateObjects.GetStateObject` strippé. IMGUI abandonné définitivement car non utilisé par le jeu et fortement strippé. Référence ajoutée à `Refs/UnityEngine.UI.dll`. UI = Canvas + Image + Text + Button + InputField (assemblies garanties présentes car le jeu les utilise).
- **v1.1.1** — **Fix font search IL2CPP** : `Scene.GetRootGameObjects()` strippé dans le build MiSide (Method not found). Remplacé par `Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"/"Arial.ttf")` (immune au stripping). Fallback `Camera.main.transform.root` conservé.
- **v1.1.1** — **Fix DontDestroyOnLoad sur Canvas** : warning Unity "DontDestroyOnLoad only works for root GameObjects" éliminé. Le Canvas est désormais parenté au bootstrap (déjà persistant) au lieu d'appeler `DontDestroyOnLoad` séparément. Persistance héritée via parent — ScreenSpaceOverlay non affecté.
- **v1.1.3** — **Élimination warning `DontDestroyOnLoad only works for root GameObjects`** : cause = `SpawnRecovery` créait un GO + DDoL en pleine transition de scène depuis les postfix `LoadScene*`. Supprimé entièrement (suppression de `BootstrapRecovery.cs`, `SpawnRecovery`, et 7 patches `LoadScene*` postfix). Le patch `Internal_SceneLoaded` couvre déjà 100 % des transitions de scène.
- **v1.1.3** — **Déduplication EventSystem** : après `BuildCanvas`, scanner la hiérarchie du canvas et désactiver tout `EventSystem` accidentellement créé par l'ajout d'`InputField` (le jeu en a déjà un). Élimine le warning Unity "There can be only one active Event System."
- **v1.1.4** — **Fix `Animator.parameters` strippé** : la propriété `Animator.parameters` (`get_parameters()`) n'existe pas en IL2CPP MiSide. `AvatarAnimatorSync.Initialize` enveloppé dans try/catch ; en cas d'échec, mode best-effort qui tente SetFloat/SetBool sur tous les noms candidats (les paramètres absents émettent un warning Unity benin).
- **v1.1.4** — **Fix `MonoBehaviour.StartCoroutine(IEnumerator)` strippé** : la surcharge managée existe dans la référence Unity mais est strippée par IL2CPP MiSide → `MissingMethodException` à chaque clic sur "Créer une room". Solution : forcer l'utilisation de l'extension BepInEx via appel STATIQUE explicite `MonoBehaviourExtensions.StartCoroutine(this, routine)` (le `using` seul ne suffit pas car la résolution C# privilégie l'instance method). Appliqué dans `RoomManager`.
- **v1.1.5** — **Fix `Animator.get_parameters()` strippé (récurrence)** : v1.1.4 enveloppait l'appel dans try/catch mais en IL2CPP la résolution échoue **avant** que le catch ne prenne effet (MissingMethodException levée au JIT du foreach). `AvatarAnimatorSync.Initialize` réécrit en boucle `for (i < parameterCount)` + `_animator.GetParameter(i)` — deux API IL2CPP-safe constatées présentes dans le dump MiSide. Le fallback best-effort reste actif si `parameterCount` lui-même venait à échouer.
- **v1.1.5** — **Fix `UnityWebRequest..ctor(string, string)` strippé** : le constructeur à 2 arguments (url, method) est strippé par IL2CPP MiSide → `MissingMethodException` lors du POST `/register` au relais. Solution : utiliser la factory `UnityWebRequest.Put(url, byte[])` (factory survivante au stripping) puis forcer `req.method = "POST"`. Astuce IL2CPP standard pour conserver corps binaire + content-type custom sans dépendre du ctor strippé. Appliqué dans `RoomManager.RegisterCoroutine`.
- **v1.1.6** — **Fix `UnityWebRequest.Put(string, byte[])` strippé** : v1.1.5 a montré que `Put` est aussi strippé (le jeu MiSide n'effectue jamais de PUT, donc Unity le retire). Seules les factories réellement utilisées par le jeu survivent. Solution finale : créer la requête via `UnityWebRequest.Get(url)` (factory déjà validée fonctionnelle dans `ResolveCoroutine`), puis muter les propriétés `method = kHttpVerbPOST`, `uploadHandler = new UploadHandlerRaw(body)`, `downloadHandler`, `timeout`, header `Content-Type`. Les setters de UnityWebRequest font partie du noyau de la classe et sont préservés.
- **v1.1.7** — **Fix `UploadHandlerRaw..ctor(Byte[])` strippé + bascule en GET** : v1.1.6 a révélé que MiSide a TOUTES les classes d'upload retirées (ctor `UploadHandlerRaw`, ctor `UnityWebRequest(string,string)`, `Put`). Conclusion : impossible d'émettre un POST en IL2CPP-safe. Solution architecturale : ajouter un alias `GET /register/:code/:port` côté relais Node.js (sans body), et basculer `RegisterCoroutine` en `UnityWebRequest.Get(url)` pur — la seule factory qui survit. `build_release.sh` synchronise désormais `relay-server.js` dans le zip de release pour que les utilisateurs reçoivent la nouvelle route. Les anciens hôtes qui souhaitent rester sur POST sont toujours compatibles (l'endpoint POST original est conservé).
- **v1.1.8** — **Fix `DownloadHandlerBuffer..ctor()` strippé : abandon total de UnityWebRequest** : v1.1.7 a montré que `UnityWebRequest.Get(url)` lui-même crash car son implémentation interne appelle `new DownloadHandlerBuffer()` qui est strippé. L'API `UnityWebRequest` est INUTILISABLE dans MiSide IL2CPP. Solution radicale : `RoomManager` réécrit en utilisant `System.Net.Http.HttpClient` — code .NET 6 pur, complètement immune au stripping IL2CPP. Pattern : la coroutine lance un `Thread` background qui exécute `_http.GetAsync(url)`, et attend (`yield return null`) le flag `done` avant d'invoquer le callback sur le main thread Unity. Helper interne `HttpGetAsync(url, timeout, onComplete)` factorise les 3 coroutines (Register/Resolve/Unregister). Ajout d'un alias `GET /room/:code/delete` côté relais pour rester symétrique (uniquement GET). Validé côté serveur : `curl /register/.../...`, `/resolve/...`, `/room/.../delete` répondent correctement.
- **v1.2.0** — **Rebranding "MiSide Together" + UI English + redesign** :
  - Renommage du mod : "MiSide Co-op" → **"MiSide Together"** (PLUGIN_NAME). GUID `com.miside.coop` conservé pour compatibilité BepInEx (dédoublonnage par GUID).
  - **Localisation complète en anglais** : tous les strings UI (titre, sous-titre, labels, boutons, status), HUD, et logs visibles côté joueur ont été traduits. Les logs internes des relais Node.js restent FR (pas visibles utilisateur final).
  - **Refonte palette Mita** : pink/magenta (#FF6FA8) comme accent au lieu du jaune, fond purple-black très sombre (#14101A), bordures violet profond (#2A1F38). Boutons avec hover violet plus contrasté.
  - **Layout amélioré** : fenêtre 680×600 (était 620×540), titre 28pt bold + sous-titre "CO-OP MULTIPLAYER MOD", barre d'accent pink fine sous le titre, footer "v1.2.0 · Press F8 to toggle this menu", code room en 64pt bold, espacements augmentés (boutons 56px → marges 28→36 latéraux, 28→40 bottom).
- **v1.2.2 → v1.2.6** — **Fix `Texture2D.SetPixels32(Color32[])` strippé/manquant en IL2CPP MiSide** :
  - **Symptôme** : `System.MissingMethodException: Method not found: 'Void UnityEngine.Texture2D.SetPixels32(UnityEngine.Color32[])'` levée dans `CoopMenuUI.GetRoundedSprite()` au premier appel `ApplyRounded`, crashant l'init du menu.
  - **Root cause** : la signature `SetPixels32(Il2CppStructArray<Color32>)` est résolue côté JIT *avant* l'entrée dans le try/catch du caller. Conséquence : le try/catch INTERNE à `GetRoundedSprite` n'attrape rien, et c'est le frame appelant (`ApplyRounded` → `BuildPanel`) qui prend l'exception.
  - **Fix v1.2.2 → v1.2.6** : isolation progressive du call risqué dans un helper `TryApplyPixels32` séparé (le JIT du helper échoue tout seul, son appelant `GetRoundedSprite` catche correctement). Ajout d'un second filet de sécurité dans `ApplyRounded` (try/catch englobant qui retombe sur des coins droits si la génération de sprite échoue). Le menu reste fonctionnel même sans rounded corners.
  - **v1.2.6** : build final livré — `MiSideCoop_v1.2.6.zip` (116KB) au `/app/MiSideCoop_release/`. Build dotnet 6.0.428 : 0 warnings, 0 errors.

- **v1.2.1** — **Fix click pass-through + rounded corners + polish UI** :
  - **Bug critique fixé** : v1.2.0 le menu disparaissait après clic "Create a room" → cause double = (a) absence de `GraphicRaycaster` sur notre canvas → clics passaient à travers vers le menu MiSide en-dessous (qui activait une transition de scène/option) ; (b) cascade de clics dans `HandleButtonClicks` qui propageait le même `mouseDown` aux boutons des panels activés par `ShowState()` dans le `OnClick`. **Fixes** : ajout de `GraphicRaycaster` + `raycastTarget=true` sur overlay et boutons, et `return` après le premier clic traité dans la boucle.
  - **Rounded corners IL2CPP-safe** : helper `GetRoundedSprite()` génère runtime une texture 64×64 avec coins arrondis (rayon 16px) + 1-px antialiasing, exposée comme Sprite 9-slice (border = 16,16,16,16). Appliqué à la fenêtre, sa bordure, l'input field, le HUD pill, et tous les boutons via `Image.type = Sliced`. Texture2D + Sprite.Create sont massivement utilisés par MiSide → confirmés non-strippés. Fallback try/catch silencieux vers coins droits si jamais.
  - **HUD avec fond pill arrondi** au lieu de texte nu (lisibilité sur scènes claires), taille texte 15pt (vs 18pt).
  - **Boutons en weight Normal** (vs Bold) à 17pt — match l'esthétique du menu MiSide natif (regular all-caps).

- **v1.4.3** — **Sync skins/vêtements + lancement simultané + bouton DÉMARRER (HOST + GUEST)** :
  - **Modèle 3D réel des deux côtés** : nouveau `RealMcCloner.cs` qui `UnityEngine.Object.Instantiate` le GameObject MC MiSide local (mesh, SkinnedMeshRenderer, Animator, bones, materials) pour produire le fantôme du peer à la place des capsule + sphère synthétiques (v1.3.x → v1.4.x). Strip défensif : Camera/AudioListener enfants supprimés, CharacterController/Rigidbody → kinematic, colliders → trigger, MonoBehaviour MiSide non-MiSideCoop → `.enabled = false` (Destroy trop dangereux car auto-références). DontDestroyOnLoad pour survivre aux scene transitions. Fallback automatique sur `CreateDefaultHumanoid` si Instantiate échoue (rare en IL2CPP) ou si le MC n'est pas encore en scène.
  - **Lancement simultané "Démarrer (Host + Guest)"** : nouveau `MsgId.GameLaunch = 7` + `GameLaunchMessage` réseau + `BroadcastGameLaunch()` côté host + handler `OnGameLaunch` côté guest. Nouveau bouton "DÉMARRER (HOST + GUEST)" dans le panel CreateRoom (visuel **grisé** tant qu'aucun guest connecté, **activé** quand `ConnectedPlayerName` est rempli). Au clic : (1) broadcast `GameLaunchMessage` au guest **avant** clic local pour ne pas perdre le message si le scene-change tue la pump TCP ; (2) `MenuButtonClicker.ClickNewGame()` localement ; (3) le guest reçoit le message et invoque pareillement son bouton "Nouvelle Partie". Le bouton est trouvé via walk depuis `Camera.main.transform.root` (puis fallback Canvas/Menu/MainMenu) + match case-insensitive sur libellés FR/EN/RU/ES (`NOUVELLE PARTIE`, `NEW GAME`, `НОВАЯ ИГРА`, `NUEVA PARTIDA`, etc.).
  - **UI menu améliorée** : le status text du panel Create se rafraîchit en temps réel ("Guest connected: <name>" → "Launching game on both sides…"). Le bouton DÉMARRER passe automatiquement de disabled → enabled dès que le guest s'identifie via `RoomJoinMessage`.
  - **Build** : 0 warnings, 0 errors. Release `/app/MiSideCoop_release/MiSideCoop_v1.4.3.zip` (128 KB, DLL 104 KB).

## 🔧 Credentials / Secrets
N/A (mode peer-to-peer, pas d'authentification).

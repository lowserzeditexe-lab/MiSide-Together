#====================================================================================================
# START - Testing Protocol - DO NOT EDIT OR REMOVE THIS SECTION
#====================================================================================================

# THIS SECTION CONTAINS CRITICAL TESTING INSTRUCTIONS FOR BOTH AGENTS
# BOTH MAIN_AGENT AND TESTING_AGENT MUST PRESERVE THIS ENTIRE BLOCK

# Communication Protocol:
# If the `testing_agent` is available, main agent should delegate all testing tasks to it.
#
# You have access to a file called `test_result.md`. This file contains the complete testing state
# and history, and is the primary means of communication between main and the testing agent.
#
# Main and testing agents must follow this exact format to maintain testing data. 
# The testing data must be entered in yaml format Below is the data structure:
# 
## user_problem_statement: {problem_statement}
## backend:
##   - task: "Task name"
##     implemented: true
##     working: true  # or false or "NA"
##     file: "file_path.py"
##     stuck_count: 0
##     priority: "high"  # or "medium" or "low"
##     needs_retesting: false
##     status_history:
##         -working: true  # or false or "NA"
##         -agent: "main"  # or "testing" or "user"
##         -comment: "Detailed comment about status"
##
## frontend:
##   - task: "Task name"
##     implemented: true
##     working: true  # or false or "NA"
##     file: "file_path.js"
##     stuck_count: 0
##     priority: "high"  # or "medium" or "low"
##     needs_retesting: false
##     status_history:
##         -working: true  # or false or "NA"
##         -agent: "main"  # or "testing" or "user"
##         -comment: "Detailed comment about status"
##
## metadata:
##   created_by: "main_agent"
##   version: "1.0"
##   test_sequence: 0
##   run_ui: false
##
## test_plan:
##   current_focus:
##     - "Task name 1"
##     - "Task name 2"
##   stuck_tasks:
##     - "Task name with persistent issues"
##   test_all: false
##   test_priority: "high_first"  # or "sequential" or "stuck_first"
##
## agent_communication:
##     -agent: "main"  # or "testing" or "user"
##     -message: "Communication message between agents"

# Protocol Guidelines for Main agent
#
# 1. Update Test Result File Before Testing:
#    - Main agent must always update the `test_result.md` file before calling the testing agent
#    - Add implementation details to the status_history
#    - Set `needs_retesting` to true for tasks that need testing
#    - Update the `test_plan` section to guide testing priorities
#    - Add a message to `agent_communication` explaining what you've done
#
# 2. Incorporate User Feedback:
#    - When a user provides feedback that something is or isn't working, add this information to the relevant task's status_history
#    - Update the working status based on user feedback
#    - If a user reports an issue with a task that was marked as working, increment the stuck_count
#    - Whenever user reports issue in the app, if we have testing agent and task_result.md file so find the appropriate task for that and append in status_history of that task to contain the user concern and problem as well 
#
# 3. Track Stuck Tasks:
#    - Monitor which tasks have high stuck_count values or where you are fixing same issue again and again, analyze that when you read task_result.md
#    - For persistent issues, use websearch tool to find solutions
#    - Pay special attention to tasks in the stuck_tasks list
#    - When you fix an issue with a stuck task, don't reset the stuck_count until the testing agent confirms it's working
#
# 4. Provide Context to Testing Agent:
#    - When calling the testing agent, provide clear instructions about:
#      - Which tasks need testing (reference the test_plan)
#      - Any authentication details or configuration needed
#      - Specific test scenarios to focus on
#      - Any known issues or edge cases to verify
#
# 5. Call the testing agent with specific instructions referring to test_result.md
#
# IMPORTANT: Main agent must ALWAYS update test_result.md BEFORE calling the testing agent, as it relies on this file to understand what to test next.

#====================================================================================================
# END - Testing Protocol - DO NOT EDIT OR REMOVE THIS SECTION
#====================================================================================================



#====================================================================================================
# Testing Data - Main Agent and testing sub agent both should log testing data below this section
#====================================================================================================

user_problem_statement: >
  Plugin BepInEx 6 IL2CPP pour MiSide (mod co-op).
  Le bootstrap GameObject est détruit lors du passage splash→titre car DontDestroyOnLoad
  était appelé depuis Load() (trop tôt). L'utilisateur veut :
  1. Ré-instancier automatiquement le bootstrap après chaque changement de scène.
  2. Ouvrir le menu directement au lancement du jeu (sans F8).

backend: []

frontend:
  - task: "Fix bootstrap DontDestroyOnLoad + auto-affichage menu"
    implemented: true
    working: "NA"
    file: "MiSideCoop/Plugin/CoopBootstrap.cs"
    stuck_count: 0
    priority: "high"
    needs_retesting: false
    status_history:
      - working: "NA"
        agent: "main"
        comment: >
          Corrections appliquées :
          1. DontDestroyOnLoad déplacé dans CoopBootstrap.Awake() avec transform.SetParent(null) explicite.
          2. Singleton Instance nettoyé dans OnDestroy() sur tous les composants (CoopNetworkManager, RoomManager, GameStateSync).
          3. Nouveau BootstrapRecovery MonoBehaviour créé à chaque LoadScene(Single) via ScenePatch Postfix.
             Survit à la transition, appelle EnsureBootstrap() dans Start(), puis s'auto-détruit.
          4. Menu _state = MenuState.Main déjà présent → menu ouvert automatiquement au lancement.
          5. Build réussi : MiSideCoop.dll 52224 octets, 0 warnings, 0 errors.
          Note : test in-game non possible dans cet environnement.

  - task: "Fix MissingMethodException SetPixels32(Color32[]) — UI build crash v1.2.1"
    implemented: true
    working: true
    file: "MiSideCoop/Plugin/UI/CoopMenuUI.cs"
    stuck_count: 0
    priority: "high"
    needs_retesting: false
    status_history:
      - working: true
        agent: "user"
        comment: >
          v1.2.2 testée en jeu : "Rounded sprite gen failed... Falling back to
          square corners" loggé une fois, UI construite, menu fonctionnel.
          Plus de crash. ✅

  - task: "v1.2.3 — Rounded corners via SetPixel, Google Sans font, Mita bug fix"
    implemented: true
    working: "NA"
    file: "MiSideCoop/Plugin/UI/CoopMenuUI.cs, MiSideCoop/Plugin/Network/CoopNetworkManager.cs"
    stuck_count: 0
    priority: "high"
    needs_retesting: false
    status_history:
      - working: "NA"
        agent: "main"
        comment: >
          3 améliorations v1.2.3 :

          1) COINS ARRONDIS — Réimplémentation TryApplyPixels32 :
             SetPixel(int x, int y, Color c) un par un (4096 itérations 64×64).
             Aucun marshalling d'array → contourne le problème IL2CPP de
             SetPixels32(Color32[]) absent. Coût négligeable (1× au boot).

          2) GOOGLE SANS (et fonts modernes) — Nouvelle priorité TryFindFontInScene :
             a. Tente Font.CreateDynamicFontFromOSFont sur ["Google Sans",
                "Product Sans", "Roboto", "Segoe UI Variable", "Segoe UI",
                "Calibri"] avec heuristique de matching de nom (rejette les
                fallbacks Unity silencieux).
             b. Flag sticky _osFontApiBroken si l'API est strippée → cascade
                directe vers Arial built-in sans spam de logs.
             c. Fallback Arial.ttf / LegacyRuntime.ttf / scan scène (existant).
             Note utilisateur : pour bénéficier de Google Sans, l'installer sur
             Windows depuis https://fonts.google.com/specimen/Google+Sans.

          3) BUG MITA "Create Room" — FindMCGameObject réécrit :
             Root cause : l'ancienne liste de noms contenait "Person" et
             "Player" qui matchaient la GameObject de Mita dans le menu
             principal. SpawnPlayer1Local lui collait alors un Player1Avatar +
             activait EnsureCameraActive → caméra enfant de Mita activée,
             vue altérée + Update() interférant avec son transform.
             Fix :
             a. FindMCGameObject utilise d'abord AccessTools.TypeByName("PlayerMove")
                + FindObjectOfType — composant unique du vrai MC MiSide.
             b. Liste de noms restreinte : "male_mc", "MC", "PlayerCharacter",
                "MainCharacter", "PlayerController". "Person" et "Player"
                retirés. Tag fallback retiré.
             c. SpawnPlayer1Local/Remote : si null → defer (pas de
                CreateDefaultHumanoid en menu). Retry toutes les 2s dans
                Update() jusqu'à ce que PlayerMove apparaisse (= en jeu).

          Build : MiSideCoop.dll 0 warn / 0 err. Package : v1.2.3.zip (112 KB).
          Test in-game à valider par l'utilisateur :
            • Menu co-op avec coins arrondis pourpres (visible immédiatement)
            • Police Google Sans si installée, sinon Segoe UI/Roboto, sinon Arial
            • "Create Room" depuis menu → Mita ne bouge plus (avatar deferred)

metadata:
  created_by: "main_agent"
  version: "1.0"
  test_sequence: 1
  run_ui: false

test_plan:
  current_focus: []
  stuck_tasks: []
  test_all: false
  test_priority: "high_first"

agent_communication:
  - agent: "main"
    message: >
      Fix bootstrap DontDestroyOnLoad IL2CPP + mécanisme de récupération automatique.
      DLL recompilé et repackagé dans MiSideCoop_release/MiSideCoop_v1.0.0.zip.
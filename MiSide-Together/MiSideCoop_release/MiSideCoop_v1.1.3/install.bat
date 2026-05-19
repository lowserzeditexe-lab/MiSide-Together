@echo off
setlocal EnableDelayedExpansion
chcp 65001 > nul
title MiSide Co-op Mod v1.0.0 - Installateur Windows

echo.
echo ==================================================================
echo    MiSide Co-op Mod v1.0.0 - Installation automatique
echo ==================================================================
echo.

set "SCRIPT_DIR=%~dp0"

:: ============================================================
::  ETAPE 1 : Detection du dossier MiSide
:: ============================================================
echo [1/6] Detection de MiSide...
set "MISIDE_PATH="
set "GAME_EXE=MiSideFull.exe"
set "GAME_DATA=MiSideFull_Data"

:: Registre Steam
for /f "tokens=2*" %%a in ('reg query "HKLM\SOFTWARE\WOW6432Node\Valve\Steam" /v "InstallPath" 2^>nul') do set "STEAM_REG=%%b"
if defined STEAM_REG (
    if exist "!STEAM_REG!\steamapps\common\MiSide\!GAME_EXE!" (
        set "MISIDE_PATH=!STEAM_REG!\steamapps\common\MiSide"
    )
)

:: Chemins Steam courants
if not defined MISIDE_PATH (
    for %%P in (
        "C:\Program Files (x86)\Steam\steamapps\common\MiSide"
        "C:\Program Files\Steam\steamapps\common\MiSide"
        "D:\SteamLibrary\steamapps\common\MiSide"
        "E:\SteamLibrary\steamapps\common\MiSide"
        "F:\SteamLibrary\steamapps\common\MiSide"
        "G:\SteamLibrary\steamapps\common\MiSide"
        "D:\Steam\steamapps\common\MiSide"
        "E:\Steam\steamapps\common\MiSide"
    ) do (
        if exist "%%~P\!GAME_EXE!" (
            set "MISIDE_PATH=%%~P"
        )
    )
)

:: Saisie manuelle si toujours pas trouve
if not defined MISIDE_PATH (
    echo      [!] MiSide n'a pas ete detecte automatiquement.
    set /p "MISIDE_PATH=      Chemin complet du dossier MiSide : "
)

if not exist "!MISIDE_PATH!\!GAME_EXE!" (
    echo.
    echo [ERREUR] !GAME_EXE! introuvable dans : !MISIDE_PATH!
    echo          Verifiez le chemin et reessayez.
    pause & exit /b 1
)
echo      [OK] MiSide trouve : !MISIDE_PATH!

set "BEPINEX_PATH=!MISIDE_PATH!\BepInEx"
set "PLUGIN_DIR=!BEPINEX_PATH!\plugins"

:: ============================================================
::  ETAPE 2 : Installation BepInEx 6 IL2CPP si absent
:: ============================================================
echo.
echo [2/6] Verification de BepInEx 6 IL2CPP...

if exist "!BEPINEX_PATH!\core\BepInEx.Core.dll" (
    echo      [OK] BepInEx deja installe.
    goto :bepinex_done
)

echo      [INFO] BepInEx non detecte. Telechargement automatique...
set "BEPINEX_URL=https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755%%2B3fab71a.zip"
set "BEPINEX_ZIP=%TEMP%\BepInEx_il2cpp.zip"

powershell -NoProfile -Command ^
  "$ProgressPreference='SilentlyContinue'; Invoke-WebRequest -Uri '!BEPINEX_URL!' -OutFile '!BEPINEX_ZIP!' -UseBasicParsing"

if not exist "!BEPINEX_ZIP!" (
    echo [ERREUR] Telechargement echoue.
    echo          Telechargez manuellement :
    echo          https://builds.bepinex.dev/projects/bepinex_be
    echo          ^(BepInEx-Unity.IL2CPP-win-x64^)
    echo          Extrayez dans : !MISIDE_PATH!
    pause & exit /b 1
)

echo      [INFO] Extraction de BepInEx...
powershell -NoProfile -Command ^
  "Expand-Archive -Path '!BEPINEX_ZIP!' -DestinationPath '!MISIDE_PATH!' -Force"
del "!BEPINEX_ZIP!" 2>nul
echo      [OK] BepInEx 6 IL2CPP installe.

:: Premier lancement requis pour generer les interop assemblies
echo.
echo ==================================================================
echo  ETAPE OBLIGATOIRE : Premier lancement de MiSide
echo ==================================================================
echo.
echo  BepInEx doit generer les assemblies interop IL2CPP au premier
echo  lancement du jeu ^(peut prendre 1-3 minutes^).
echo.
echo  Je vais maintenant lancer MiSide. ATTENDEZ que le menu principal
echo  apparaisse, puis QUITTEZ le jeu. Le script reprendra automatiquement.
echo.
pause

echo      [INFO] Lancement de MiSide...
start "" /wait "!MISIDE_PATH!\!GAME_EXE!"
echo      [OK] Jeu ferme. Reprise de l'installation.

:bepinex_done

:: ============================================================
::  ETAPE 3 : Verification que les interop ont ete generes
:: ============================================================
echo.
echo [3/6] Verification des assemblies interop IL2CPP...
if not exist "!BEPINEX_PATH!\interop" (
    echo      [AVERT] Le dossier BepInEx\interop n'existe pas encore.
    echo              Il sera cree au prochain lancement du jeu.
) else (
    echo      [OK] Assemblies interop presentes.
)

:: ============================================================
::  ETAPE 4 : Copie du DLL MiSideCoop
:: ============================================================
echo.
echo [4/6] Installation du plugin MiSideCoop...
mkdir "!PLUGIN_DIR!" 2>nul

set "DLL_SOURCE="
if exist "!SCRIPT_DIR!BepInEx\plugins\MiSideCoop\MiSideCoop.dll" (
    set "DLL_SOURCE=!SCRIPT_DIR!BepInEx\plugins\MiSideCoop\MiSideCoop.dll"
) else if exist "!SCRIPT_DIR!MiSideCoop.dll" (
    set "DLL_SOURCE=!SCRIPT_DIR!MiSideCoop.dll"
)

if not defined DLL_SOURCE (
    echo [ERREUR] MiSideCoop.dll introuvable.
    echo          Verifiez que vous executez install.bat depuis le ZIP extrait.
    pause & exit /b 1
)

mkdir "!PLUGIN_DIR!\MiSideCoop" 2>nul
copy /y "!DLL_SOURCE!" "!PLUGIN_DIR!\MiSideCoop\MiSideCoop.dll" >nul
echo      [OK] MiSideCoop.dll installe dans !PLUGIN_DIR!\MiSideCoop\

:: ============================================================
::  ETAPE 5 : Pare-feu Windows (port 7777 TCP)
:: ============================================================
echo.
echo [5/6] Configuration du pare-feu Windows ^(port TCP 7777^)...
netsh advfirewall firewall delete rule name="MiSide Co-op" >nul 2>&1
netsh advfirewall firewall add rule name="MiSide Co-op" protocol=TCP dir=in localport=7777 action=allow >nul 2>&1
if !errorlevel!==0 (
    echo      [OK] Regle pare-feu creee.
) else (
    echo      [AVERT] Echec de la modification du pare-feu.
    echo              Lancez install.bat en tant qu'administrateur,
    echo              ou ouvrez manuellement le port TCP 7777.
)

:: ============================================================
::  ETAPE 6 : Serveur relay Node.js (optionnel)
:: ============================================================
echo.
echo [6/6] Configuration du serveur relay...
if exist "!SCRIPT_DIR!relay-server.js" (
    copy /y "!SCRIPT_DIR!relay-server.js" "!MISIDE_PATH!\relay-server.js" >nul
    if exist "!SCRIPT_DIR!package.json" (
        copy /y "!SCRIPT_DIR!package.json" "!MISIDE_PATH!\package.json" >nul
    )
    echo      [OK] relay-server.js copie dans !MISIDE_PATH!

    where node >nul 2>&1
    if !errorlevel!==0 (
        echo      [INFO] Node.js detecte. Installation des dependances...
        pushd "!MISIDE_PATH!"
        call npm install --silent --no-audit --no-fund 2>nul
        popd
        echo      [OK] Dependances installees ^(express^).
    ) else (
        echo      [AVERT] Node.js non detecte sur ce systeme.
        echo              Telechargez-le depuis : https://nodejs.org
        echo              Puis lancez : npm install ^&^& node relay-server.js
    )
)

:: ============================================================
::  Resume final
:: ============================================================
echo.
echo ==================================================================
echo    Installation terminee avec succes !
echo ==================================================================
echo.
echo  Pour jouer :
echo    1. Lancez MiSide ^(!GAME_EXE!^)
echo    2. Appuyez sur F8 pour ouvrir le menu Co-op
echo    3. Hote : "Creer une room" -^> partagez le code a 6 chars
echo       Invite : "Rejoindre" -^> saisissez le code
echo.
echo  Logs : !BEPINEX_PATH!\LogOutput.log
echo  Config : !BEPINEX_PATH!\config\com.miside.coop.cfg
echo.
echo  Serveur de relay ^(une seule machine accessible publiquement^) :
echo    cd "!MISIDE_PATH!"
echo    node relay-server.js
echo.
pause

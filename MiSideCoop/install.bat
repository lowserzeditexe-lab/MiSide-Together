@echo off
setlocal EnableDelayedExpansion
chcp 65001 > nul
title MiSide Co-op Mod v1.0.0 — Installateur Windows

echo ╔══════════════════════════════════════════════════╗
echo ║   MiSide Co-op Mod v1.0.0 — Installation        ║
echo ╚══════════════════════════════════════════════════╝
echo.

:: ── 1. Détection du dossier MiSide ─────────────────────────────────────────
set "MISIDE_PATH="

:: Registre Steam
for /f "tokens=2*" %%a in ('reg query "HKLM\SOFTWARE\WOW6432Node\Valve\Steam" /v "InstallPath" 2^>nul') do set "STEAM_REG=%%b"
if defined STEAM_REG (
    if exist "!STEAM_REG!\steamapps\common\MiSide\MiSide.exe" (
        set "MISIDE_PATH=!STEAM_REG!\steamapps\common\MiSide"
        echo [OK] MiSide détecté via registre Steam : !MISIDE_PATH!
        goto :found
    )
)

:: Chemins Steam courants
for %%P in (
    "C:\Program Files (x86)\Steam\steamapps\common\MiSide"
    "C:\Program Files\Steam\steamapps\common\MiSide"
    "D:\SteamLibrary\steamapps\common\MiSide"
    "E:\SteamLibrary\steamapps\common\MiSide"
    "F:\SteamLibrary\steamapps\common\MiSide"
) do (
    if exist "%%~P\MiSide.exe" (
        set "MISIDE_PATH=%%~P"
        echo [OK] MiSide détecté : !MISIDE_PATH!
        goto :found
    )
)

:: Saisie manuelle
echo [!] MiSide n'a pas été détecté automatiquement.
set /p "MISIDE_PATH=Chemin complet du dossier MiSide (ex: C:\Games\MiSide) : "
if not exist "!MISIDE_PATH!\MiSide.exe" (
    echo.
    echo [ERREUR] MiSide.exe introuvable dans : !MISIDE_PATH!
    echo          Vérifiez le chemin et réessayez.
    pause & exit /b 1
)

:found
set "BEPINEX_PATH=!MISIDE_PATH!\BepInEx"
set "PLUGIN_DIR=!BEPINEX_PATH!\plugins\MiSideCoop"
set "SCRIPT_DIR=%~dp0"

echo.
:: ── 2. Installation BepInEx 6 si absent ─────────────────────────────────────
if not exist "!BEPINEX_PATH!\core\BepInEx.Core.dll" (
    echo [INFO] BepInEx 6 non détecté — téléchargement en cours...
    echo.

    set "BEPINEX_URL=https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.2/BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip"
    set "BEPINEX_ZIP=!MISIDE_PATH!\BepInEx_tmp.zip"

    powershell -NoProfile -Command ^
      "try { Invoke-WebRequest -Uri '!BEPINEX_URL!' -OutFile '!BEPINEX_ZIP!' -UseBasicParsing; Write-Host 'DOWNLOAD_OK' } catch { Write-Host ('DOWNLOAD_ERR:' + $_.Exception.Message) }" > "!TEMP!\bepinex_dl.txt" 2>&1
    set /p DL_RESULT=<"!TEMP!\bepinex_dl.txt"

    if not exist "!BEPINEX_ZIP!" (
        echo [ERREUR] Téléchargement de BepInEx échoué.
        echo          Téléchargez manuellement depuis :
        echo          https://github.com/BepInEx/BepInEx/releases
        echo          Puis extrayez dans : !MISIDE_PATH!
        pause & exit /b 1
    )

    powershell -NoProfile -Command "Expand-Archive -Path '!BEPINEX_ZIP!' -DestinationPath '!MISIDE_PATH!' -Force"
    del "!BEPINEX_ZIP!" 2>nul

    echo [OK] BepInEx 6 installé dans !BEPINEX_PATH!
    echo.
    echo [ETAPE REQUISE] Lancez MiSide UNE FOIS pour que BepInEx
    echo                 génère sa configuration initiale,
    echo                 puis RELANCEZ install.bat.
    echo.
    pause & exit /b 0
)

echo [OK] BepInEx 6 détecté.
echo.

:: ── 3. Création des dossiers plugins ─────────────────────────────────────────
mkdir "!PLUGIN_DIR!" 2>nul
echo [OK] Dossier plugin : !PLUGIN_DIR!

:: ── 4. Copie du DLL principal ─────────────────────────────────────────────────
if exist "!SCRIPT_DIR!MiSideCoop.dll" (
    copy /y "!SCRIPT_DIR!MiSideCoop.dll" "!PLUGIN_DIR!\MiSideCoop.dll" >nul
    echo [OK] MiSideCoop.dll copié.
) else (
    echo [ERREUR] MiSideCoop.dll introuvable dans le dossier courant.
    echo          Compilez le mod d'abord :
    echo            dotnet build MiSideCoop.csproj --configuration Release
    echo          Le DLL sera dans bin\Release\MiSideCoop.dll
    pause & exit /b 1
)

:: ── 5. Copie de Mirror.dll et Telepathy.dll ───────────────────────────────────
if exist "!SCRIPT_DIR!Mirror.dll" (
    copy /y "!SCRIPT_DIR!Mirror.dll" "!PLUGIN_DIR!\Mirror.dll" >nul
    echo [OK] Mirror.dll copié.
) else (
    echo [AVERT] Mirror.dll absent — téléchargez-le depuis :
    echo         https://github.com/MirrorNetworking/Mirror/releases
    echo         Placez Mirror.dll dans : !PLUGIN_DIR!\
)

if exist "!SCRIPT_DIR!Telepathy.dll" (
    copy /y "!SCRIPT_DIR!Telepathy.dll" "!PLUGIN_DIR!\Telepathy.dll" >nul
    echo [OK] Telepathy.dll copié.
)

:: ── 6. Copie du serveur relais ────────────────────────────────────────────────
if exist "!SCRIPT_DIR!relay-server.js" (
    copy /y "!SCRIPT_DIR!relay-server.js" "!MISIDE_PATH!\relay-server.js" >nul
    echo [OK] relay-server.js copié dans !MISIDE_PATH!
)

:: ── 7. Ouverture du pare-feu (optionnel) ─────────────────────────────────────
echo.
echo [INFO] Tentative d'ouverture du port 7777 dans le pare-feu Windows...
netsh advfirewall firewall add rule name="MiSide Co-op" protocol=TCP dir=in localport=7777 action=allow > nul 2>&1
if %errorlevel%==0 (
    echo [OK] Règle pare-feu ajoutée (port TCP 7777 entrant).
) else (
    echo [AVERT] Impossible de modifier le pare-feu automatiquement.
    echo         Ouvrez manuellement le port TCP 7777 si vous êtes l'hôte.
)

:: ── 8. Résumé ─────────────────────────────────────────────────────────────────
echo.
echo ╔══════════════════════════════════════════════════╗
echo ║   Installation terminée avec succès !            ║
echo ╚══════════════════════════════════════════════════╝
echo.
echo   Lancez MiSide, puis appuyez sur F8 pour le menu co-op.
echo.
echo   Serveur de relais (optionnel mais recommandé) :
echo     node "!MISIDE_PATH!\relay-server.js"
echo.
echo   Configuration du mod :
echo     !BEPINEX_PATH!\config\com.miside.coop.cfg
echo.
pause

#!/usr/bin/env bash
# ============================================================
#  MiSide Co-op Mod v1.0.0 — Installateur Linux / macOS (Proton)
# ============================================================
set -euo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
GAME_EXE="MiSideFull.exe"

echo
echo "=================================================================="
echo "   MiSide Co-op Mod v1.0.0 — Installation automatique"
echo "=================================================================="
echo

# ---------- 1. Détection MiSide ----------
echo "[1/6] Détection de MiSide..."

MISIDE_PATH=""
CANDIDATES=(
    "$HOME/.steam/steam/steamapps/common/MiSide"
    "$HOME/.local/share/Steam/steamapps/common/MiSide"
    "$HOME/snap/steam/common/.local/share/Steam/steamapps/common/MiSide"
    "$HOME/Library/Application Support/Steam/steamapps/common/MiSide"  # macOS
    "/run/media/$USER/*/SteamLibrary/steamapps/common/MiSide"
)

for p in "${CANDIDATES[@]}"; do
    # expand globs
    for expanded in $p; do
        if [[ -f "$expanded/$GAME_EXE" ]]; then
            MISIDE_PATH="$expanded"
            break 2
        fi
    done
done

if [[ -z "$MISIDE_PATH" ]]; then
    echo "      [!] MiSide non détecté automatiquement."
    read -rp "      Chemin complet du dossier MiSide : " MISIDE_PATH
fi

if [[ ! -f "$MISIDE_PATH/$GAME_EXE" ]]; then
    echo "[ERREUR] $GAME_EXE introuvable dans : $MISIDE_PATH"
    exit 1
fi
echo "      [OK] MiSide trouvé : $MISIDE_PATH"

BEPINEX_PATH="$MISIDE_PATH/BepInEx"
PLUGIN_DIR="$BEPINEX_PATH/plugins/MiSideCoop"

# ---------- 2. Installation BepInEx 6 IL2CPP ----------
echo
echo "[2/6] Vérification de BepInEx 6 IL2CPP..."

if [[ -f "$BEPINEX_PATH/core/BepInEx.Core.dll" ]]; then
    echo "      [OK] BepInEx déjà installé."
else
    echo "      [INFO] Téléchargement de BepInEx 6 IL2CPP (Windows x64 pour Proton)..."
    BEPINEX_URL="https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755%2B3fab71a.zip"
    TMP_ZIP="$(mktemp -d)/bepinex.zip"

    if command -v curl >/dev/null 2>&1; then
        curl -sL -o "$TMP_ZIP" "$BEPINEX_URL"
    elif command -v wget >/dev/null 2>&1; then
        wget -q -O "$TMP_ZIP" "$BEPINEX_URL"
    else
        echo "[ERREUR] curl ou wget requis."
        exit 1
    fi

    if [[ ! -s "$TMP_ZIP" ]]; then
        echo "[ERREUR] Téléchargement échoué."
        exit 1
    fi

    unzip -q -o "$TMP_ZIP" -d "$MISIDE_PATH"
    rm -f "$TMP_ZIP"
    echo "      [OK] BepInEx 6 IL2CPP extrait."

    echo
    echo "=================================================================="
    echo "  ÉTAPE OBLIGATOIRE : Premier lancement de MiSide"
    echo "=================================================================="
    echo
    echo "  Lancez MiSide UNE FOIS via Steam pour que BepInEx génère ses"
    echo "  assemblies interop IL2CPP (peut prendre 1-3 minutes)."
    echo "  Quittez ensuite le jeu, puis RELANCEZ ce script."
    echo
    read -rp "  Appuyez sur Entrée pour quitter..."
    exit 0
fi

# ---------- 3. Vérification interop ----------
echo
echo "[3/6] Vérification des assemblies interop IL2CPP..."
if [[ -d "$BEPINEX_PATH/interop" ]]; then
    echo "      [OK] Assemblies interop présentes."
else
    echo "      [AVERT] Lancez MiSide une fois avant d'utiliser le mod."
fi

# ---------- 4. Copie du DLL ----------
echo
echo "[4/6] Installation du plugin MiSideCoop..."
mkdir -p "$PLUGIN_DIR"

DLL_SOURCE=""
if [[ -f "$SCRIPT_DIR/BepInEx/plugins/MiSideCoop/MiSideCoop.dll" ]]; then
    DLL_SOURCE="$SCRIPT_DIR/BepInEx/plugins/MiSideCoop/MiSideCoop.dll"
elif [[ -f "$SCRIPT_DIR/MiSideCoop.dll" ]]; then
    DLL_SOURCE="$SCRIPT_DIR/MiSideCoop.dll"
fi

if [[ -z "$DLL_SOURCE" ]]; then
    echo "[ERREUR] MiSideCoop.dll introuvable."
    exit 1
fi

cp -f "$DLL_SOURCE" "$PLUGIN_DIR/MiSideCoop.dll"
echo "      [OK] MiSideCoop.dll installé dans $PLUGIN_DIR/"

# ---------- 5. Pare-feu (Linux uniquement, ufw) ----------
echo
echo "[5/6] Configuration du pare-feu (port TCP 7777)..."
if command -v ufw >/dev/null 2>&1 && sudo -n true 2>/dev/null; then
    sudo ufw allow 7777/tcp >/dev/null 2>&1 && \
        echo "      [OK] Règle ufw créée." || \
        echo "      [AVERT] Échec ufw — ouvrez manuellement le port 7777/tcp."
else
    echo "      [AVERT] ufw non disponible ou sudo requis."
    echo "              Si vous êtes l'hôte, ouvrez manuellement TCP/7777."
fi

# ---------- 6. Serveur relay ----------
echo
echo "[6/6] Configuration du serveur relay..."
if [[ -f "$SCRIPT_DIR/relay-server.js" ]]; then
    cp -f "$SCRIPT_DIR/relay-server.js" "$MISIDE_PATH/relay-server.js"
    [[ -f "$SCRIPT_DIR/package.json" ]] && cp -f "$SCRIPT_DIR/package.json" "$MISIDE_PATH/package.json"
    echo "      [OK] relay-server.js copié."

    if command -v node >/dev/null 2>&1 && command -v npm >/dev/null 2>&1; then
        echo "      [INFO] Installation des dépendances Node..."
        (cd "$MISIDE_PATH" && npm install --silent --no-audit --no-fund >/dev/null 2>&1)
        echo "      [OK] express installé."
    else
        echo "      [AVERT] Node.js absent. Installez-le : https://nodejs.org"
    fi
fi

# ---------- Résumé ----------
echo
echo "=================================================================="
echo "   Installation terminée avec succès !"
echo "=================================================================="
echo
echo "  Pour jouer :"
echo "    1. Lancez MiSide via Steam"
echo "    2. F8 pour ouvrir le menu Co-op"
echo "    3. Créer/rejoindre une room avec un code 6 chars"
echo
echo "  Logs   : $BEPINEX_PATH/LogOutput.log"
echo "  Config : $BEPINEX_PATH/config/com.miside.coop.cfg"
echo
echo "  Serveur de relay (machine accessible publiquement) :"
echo "    cd \"$MISIDE_PATH\" && node relay-server.js"
echo

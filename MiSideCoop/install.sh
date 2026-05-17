#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
#  MiSide Co-op Mod v1.0.0 — Installateur Linux / macOS
# ──────────────────────────────────────────────────────────────────────────────
set -euo pipefail

CYAN='\033[0;36m'; GREEN='\033[0;32m'; RED='\033[0;31m'; YELLOW='\033[1;33m'; NC='\033[0m'

info()  { echo -e "${CYAN}[INFO]${NC} $*"; }
ok()    { echo -e "${GREEN}[OK]${NC}   $*"; }
warn()  { echo -e "${YELLOW}[AVERT]${NC} $*"; }
error() { echo -e "${RED}[ERREUR]${NC} $*"; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MISIDE_PATH=""

echo -e "${CYAN}╔══════════════════════════════════════════════════╗${NC}"
echo -e "${CYAN}║   MiSide Co-op Mod v1.0.0 — Installation        ║${NC}"
echo -e "${CYAN}╚══════════════════════════════════════════════════╝${NC}"
echo

# ── 1. Détection de MiSide ────────────────────────────────────────────────────
detect_miside() {
    local candidates=(
        # Linux Steam
        "$HOME/.steam/steam/steamapps/common/MiSide"
        "$HOME/.local/share/Steam/steamapps/common/MiSide"
        "$HOME/snap/steam/common/.local/share/Steam/steamapps/common/MiSide"
        # Flatpak Steam
        "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/MiSide"
        # macOS Steam
        "$HOME/Library/Application Support/Steam/steamapps/common/MiSide"
        # Chemins personnalisés courants
        "/opt/steam/steamapps/common/MiSide"
        "$HOME/Steam/steamapps/common/MiSide"
    )

    for p in "${candidates[@]}"; do
        # Exécutable Linux natif ou via Wine
        if [ -f "$p/MiSide" ] || [ -f "$p/MiSide.exe" ]; then
            MISIDE_PATH="$p"
            return 0
        fi
    done
    return 1
}

detect_miside || true

if [ -z "$MISIDE_PATH" ]; then
    warn "MiSide non détecté automatiquement."
    read -rp "Chemin complet du dossier MiSide : " MISIDE_PATH
    [ -d "$MISIDE_PATH" ] || error "Dossier introuvable : $MISIDE_PATH"
fi

ok "MiSide trouvé : $MISIDE_PATH"
echo

BEPINEX_PATH="$MISIDE_PATH/BepInEx"
PLUGIN_DIR="$BEPINEX_PATH/plugins/MiSideCoop"

# ── 2. Installation de BepInEx 6 si absent ────────────────────────────────────
if [ ! -f "$BEPINEX_PATH/core/BepInEx.Core.dll" ]; then
    info "BepInEx 6 non détecté — téléchargement..."

    # Détection de l'architecture
    OS="$(uname -s)"
    ARCH="$(uname -m)"

    if [ "$OS" = "Darwin" ]; then
        BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.2/BepInEx-Unity.IL2CPP-macos-x64-6.0.0-pre.2.zip"
    elif [ "$ARCH" = "aarch64" ] || [ "$ARCH" = "arm64" ]; then
        BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.2/BepInEx-Unity.IL2CPP-linux-arm64-6.0.0-pre.2.zip"
    else
        BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.2/BepInEx-Unity.IL2CPP-linux-x64-6.0.0-pre.2.zip"
    fi

    BEPINEX_ZIP="$MISIDE_PATH/BepInEx_tmp.zip"

    if command -v curl &>/dev/null; then
        curl -L --progress-bar "$BEPINEX_URL" -o "$BEPINEX_ZIP"
    elif command -v wget &>/dev/null; then
        wget --show-progress -O "$BEPINEX_ZIP" "$BEPINEX_URL"
    else
        error "curl ou wget requis. Installez l'un d'eux puis réessayez."
    fi

    [ -f "$BEPINEX_ZIP" ] || error "Téléchargement échoué."

    unzip -q "$BEPINEX_ZIP" -d "$MISIDE_PATH"
    rm -f "$BEPINEX_ZIP"

    ok "BepInEx 6 installé dans $BEPINEX_PATH"
    echo
    warn "Lancez MiSide UNE FOIS pour initialiser BepInEx,"
    warn "puis relancez ce script."
    exit 0
fi

ok "BepInEx 6 détecté."
echo

# ── 3. Création des dossiers plugins ─────────────────────────────────────────
mkdir -p "$PLUGIN_DIR"
ok "Dossier plugin : $PLUGIN_DIR"

# ── 4. Copie du DLL principal ─────────────────────────────────────────────────
if [ -f "$SCRIPT_DIR/MiSideCoop.dll" ]; then
    cp "$SCRIPT_DIR/MiSideCoop.dll" "$PLUGIN_DIR/MiSideCoop.dll"
    ok "MiSideCoop.dll copié."
else
    error "MiSideCoop.dll introuvable dans $SCRIPT_DIR\n       Compilez d'abord : dotnet build MiSideCoop.csproj --configuration Release"
fi

# ── 5. Mirror.dll et Telepathy.dll ────────────────────────────────────────────
if [ -f "$SCRIPT_DIR/Mirror.dll" ]; then
    cp "$SCRIPT_DIR/Mirror.dll" "$PLUGIN_DIR/Mirror.dll"
    ok "Mirror.dll copié."
else
    warn "Mirror.dll absent — téléchargez depuis :"
    warn "  https://github.com/MirrorNetworking/Mirror/releases"
    warn "  Puis copiez Mirror.dll dans $PLUGIN_DIR/"
fi

if [ -f "$SCRIPT_DIR/Telepathy.dll" ]; then
    cp "$SCRIPT_DIR/Telepathy.dll" "$PLUGIN_DIR/Telepathy.dll"
    ok "Telepathy.dll copié."
fi

# ── 6. Serveur relais ─────────────────────────────────────────────────────────
if [ -f "$SCRIPT_DIR/relay-server.js" ]; then
    cp "$SCRIPT_DIR/relay-server.js" "$MISIDE_PATH/relay-server.js"
    ok "relay-server.js copié dans $MISIDE_PATH/"
fi

# ── 7. Permissions exécutable (Linux/macOS) ───────────────────────────────────
MISIDE_EXE="$MISIDE_PATH/MiSide"
if [ -f "$MISIDE_EXE" ] && [ ! -x "$MISIDE_EXE" ]; then
    chmod +x "$MISIDE_EXE"
    ok "Permissions exécutables corrigées sur MiSide."
fi

# ── 8. Résumé ─────────────────────────────────────────────────────────────────
echo
echo -e "${CYAN}╔══════════════════════════════════════════════════╗${NC}"
echo -e "${GREEN}║   Installation terminée avec succès !            ║${NC}"
echo -e "${CYAN}╚══════════════════════════════════════════════════╝${NC}"
echo
echo -e "  Lancez MiSide → appuyez ${YELLOW}F8${NC} pour ouvrir le menu co-op."
echo
echo -e "  Serveur de relais (recommandé) :"
echo -e "    ${YELLOW}node $MISIDE_PATH/relay-server.js${NC}"
echo
echo -e "  Configuration du mod :"
echo -e "    ${YELLOW}$BEPINEX_PATH/config/com.miside.coop.cfg${NC}"
echo

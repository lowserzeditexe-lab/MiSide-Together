#!/usr/bin/env bash
# ============================================================================
# MiSide Co-op — Script de build & packaging automatique
#
# Usage : ./build_release.sh [version_optionnelle]
#
# Comportement :
#   1. Build du .dll en mode Release (.NET / dotnet CLI)
#   2. Lit la version depuis Plugin/PluginInfo.cs (ou utilise l'argument 1)
#   3. Crée un dossier MiSideCoop_release/MiSideCoop_v<X.Y.Z>/ avec la structure
#      BepInEx attendue
#   4. Zippe le tout dans MiSideCoop_release/MiSideCoop_v<X.Y.Z>.zip
#
# Si la version courante existe déjà → incrémente automatiquement le patch.
# ============================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
RELEASE_DIR="$ROOT_DIR/MiSideCoop_release"
TEMPLATE_DIR="$RELEASE_DIR/MiSideCoop_v1.0.0"
PLUGIN_INFO="$SCRIPT_DIR/Plugin/PluginInfo.cs"

# ─── Détection de la version ────────────────────────────────────────────────
if [[ $# -ge 1 ]]; then
    VERSION="$1"
else
    VERSION=$(grep PLUGIN_VERSION "$PLUGIN_INFO" | sed 's/.*"\(.*\)".*/\1/')
fi

# Si version déjà packagée → incrémente le patch (1.0.1 → 1.0.2)
while [[ -f "$RELEASE_DIR/MiSideCoop_v${VERSION}.zip" ]]; do
    IFS='.' read -r MAJ MIN PATCH <<< "$VERSION"
    PATCH=$((PATCH + 1))
    VERSION="${MAJ}.${MIN}.${PATCH}"
    echo "→ Version déjà existante, on passe à v${VERSION}"
done

echo "═══════════════════════════════════════════════"
echo " MiSide Co-op — Build v${VERSION}"
echo "═══════════════════════════════════════════════"

# ─── Mise à jour de PluginInfo.cs ──────────────────────────────────────────
sed -i "s/PLUGIN_VERSION = \".*\"/PLUGIN_VERSION = \"${VERSION}\"/" "$PLUGIN_INFO"
echo "✓ PluginInfo.cs → v${VERSION}"

# ─── Build .NET ─────────────────────────────────────────────────────────────
export PATH="$PATH:/root/.dotnet"
dotnet build "$SCRIPT_DIR/MiSideCoop.csproj" -c Release -nologo --verbosity quiet
echo "✓ Build OK : $SCRIPT_DIR/bin/Release/MiSideCoop.dll"

# ─── Préparation du dossier release ────────────────────────────────────────
DST="$RELEASE_DIR/MiSideCoop_v${VERSION}"
rm -rf "$DST"
cp -r "$TEMPLATE_DIR" "$DST"

# Remplace le DLL partout où il apparaît
find "$DST" -name "MiSideCoop.dll" -exec cp "$SCRIPT_DIR/bin/Release/MiSideCoop.dll" {} \;

# Synchronise le serveur de relais (peut évoluer entre versions, ex. v1.1.7
# a ajouté l'alias GET /register/:code/:port IL2CPP-safe).
if [[ -f "$SCRIPT_DIR/relay-server.js" ]]; then
    cp "$SCRIPT_DIR/relay-server.js" "$DST/relay-server.js"
fi

# Synchronise les sources C# (sans .git)
rsync -a --delete "$SCRIPT_DIR/Plugin/" "$DST/src/Plugin/"

# ─── Zip final ─────────────────────────────────────────────────────────────
cd "$DST"
zip -r "$RELEASE_DIR/MiSideCoop_v${VERSION}.zip" . -q
cd "$RELEASE_DIR"

SIZE=$(du -h "MiSideCoop_v${VERSION}.zip" | cut -f1)
echo "✓ ZIP créé : $RELEASE_DIR/MiSideCoop_v${VERSION}.zip ($SIZE)"
echo "═══════════════════════════════════════════════"

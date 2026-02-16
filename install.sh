#!/usr/bin/env bash
set -euo pipefail
# Detect arch, download latest release, create systemd service, write default config
echo "Soulman Linux installer"
echo "TODO: implement release download + systemd setup"
# For now: clone and build
command -v dotnet >/dev/null || { echo "Install .NET 9.0+ first: https://dot.net"; exit 1; }
INSTALL_DIR="${SOULMAN_DIR:-$HOME/.local/share/soulman}"
mkdir -p "$INSTALL_DIR"
cd "$INSTALL_DIR"
if [ -d .git ]; then git pull; else git clone https://github.com/SlimeQ/soulman.git .; fi
dotnet build src/Soulman.Linux/
echo "Run with: dotnet run --project $INSTALL_DIR/src/Soulman.Linux/"
echo "Configure with: dotnet run --project $INSTALL_DIR/src/Soulman.Setup/"

#!/usr/bin/env bash
# Soulman Linux installer — sets up a systemd user service
# Usage: ./install-linux.sh [publish-dir]
# If no publish dir given, assumes current directory contains the published binary.
set -euo pipefail

PUBLISH_DIR="${1:-.}"
INSTALL_DIR="$HOME/.local/share/soulman/bin"
SERVICE_DIR="$HOME/.config/systemd/user"
BINARY="Soulman"

if [[ ! -f "$PUBLISH_DIR/$BINARY" ]]; then
    echo "Error: $PUBLISH_DIR/$BINARY not found."
    echo "Build first: dotnet publish -c Release -r linux-x64 --self-contained -o publish"
    exit 1
fi

echo "Installing Soulman to $INSTALL_DIR..."
mkdir -p "$INSTALL_DIR"
cp -r "$PUBLISH_DIR"/* "$INSTALL_DIR/"
chmod +x "$INSTALL_DIR/$BINARY"

echo "Creating systemd user service..."
mkdir -p "$SERVICE_DIR"
cat > "$SERVICE_DIR/soulman.service" <<EOF
[Unit]
Description=Soulman - Music Organization & Peer Sync
After=network.target

[Service]
Type=notify
ExecStart=$INSTALL_DIR/$BINARY
Restart=on-failure
RestartSec=10
WorkingDirectory=$INSTALL_DIR

[Install]
WantedBy=default.target
EOF

systemctl --user daemon-reload
systemctl --user enable --now soulman.service

echo "Done! Soulman is running."
echo "  Status:  systemctl --user status soulman"
echo "  Logs:    journalctl --user -u soulman -f"
echo "  Config:  ~/.config/soulman/appsettings.json"

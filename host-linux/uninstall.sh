#!/usr/bin/env bash
set -euo pipefail
APP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/nawah-host"
DESKTOP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
rm -rf -- "$APP_DIR"
rm -f -- "$HOME/.local/bin/nawah-host" "$DESKTOP_DIR/nawah-host.desktop"
echo "AI Palm Host was removed. Your settings remain in ${XDG_CONFIG_HOME:-$HOME/.config}/nawah-host"

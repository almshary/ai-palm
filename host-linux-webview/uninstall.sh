#!/usr/bin/env bash
set -euo pipefail

APP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/ai-palm"
BIN_FILE="$HOME/.local/bin/ai-palm"
DESKTOP_FILE="${XDG_DATA_HOME:-$HOME/.local/share}/applications/ai-palm.desktop"

rm -f -- "$BIN_FILE" "$DESKTOP_FILE"
rm -rf -- "$APP_DIR"
echo "AI Palm has been removed. User settings remain in ~/.config/ai-palm."

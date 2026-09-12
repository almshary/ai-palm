#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"
OUTPUT_DIR="${1:-$REPO_DIR/dist}"
VERSION="0.1.7"
PACKAGE="AI-Palm-Linux-x64-v$VERSION"
STAGE="$(mktemp -d)"
trap 'rm -rf -- "$STAGE"' EXIT

mkdir -p "$STAGE/$PACKAGE/ui" "$OUTPUT_DIR"
cp "$SCRIPT_DIR/aipalm_linux.py" "$SCRIPT_DIR/install.sh" "$SCRIPT_DIR/uninstall.sh" "$SCRIPT_DIR/requirements.txt" "$SCRIPT_DIR/README.md" "$SCRIPT_DIR/ai-palm-mark.png" "$STAGE/$PACKAGE/"
cp "$REPO_DIR/host-win-webview/ui/index.html" "$REPO_DIR/host-win-webview/ui/styles.css" "$REPO_DIR/host-win-webview/ui/layout-fixes.css" "$REPO_DIR/host-win-webview/ui/app.js" "$STAGE/$PACKAGE/ui/"
cp "$SCRIPT_DIR/ai-palm-mark.png" "$STAGE/$PACKAGE/ui/ai-palm-mark.png"
chmod +x "$STAGE/$PACKAGE/aipalm_linux.py" "$STAGE/$PACKAGE/install.sh" "$STAGE/$PACKAGE/uninstall.sh"
tar -C "$STAGE" -czf "$OUTPUT_DIR/$PACKAGE.tar.gz" "$PACKAGE"
echo "$OUTPUT_DIR/$PACKAGE.tar.gz"

#!/usr/bin/env bash
set -euo pipefail

APP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/ai-palm"
BIN_DIR="$HOME/.local/bin"
DESKTOP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

command -v python3 >/dev/null || { echo "Python 3 is required."; exit 1; }
python3 -c 'import gi; gi.require_version("Gtk", "3.0"); gi.require_version("WebKit2", "4.1")' 2>/dev/null || {
  echo "WebKitGTK is required. On Ubuntu/Debian run:"
  echo "sudo apt install python3-venv python3-gi python3-gi-cairo gir1.2-gtk-3.0 gir1.2-webkit2-4.1"
  exit 1
}

UI_DIR="$SCRIPT_DIR/ui"
if [[ ! -f "$UI_DIR/index.html" ]]; then
  UI_DIR="$SCRIPT_DIR/../host-win-webview/ui"
fi
[[ -f "$UI_DIR/index.html" ]] || { echo "Shared AI Palm UI files are missing."; exit 1; }

mkdir -p "$APP_DIR/ui" "$BIN_DIR" "$DESKTOP_DIR"
cp "$SCRIPT_DIR/aipalm_linux.py" "$SCRIPT_DIR/requirements.txt" "$SCRIPT_DIR/ai-palm-mark.png" "$APP_DIR/"
cp "$UI_DIR/index.html" "$UI_DIR/styles.css" "$UI_DIR/layout-fixes.css" "$UI_DIR/app.js" "$APP_DIR/ui/"
cp "$SCRIPT_DIR/ai-palm-mark.png" "$APP_DIR/ui/ai-palm-mark.png"
python3 -m venv --system-site-packages "$APP_DIR/venv"
"$APP_DIR/venv/bin/python" -m pip install --quiet --upgrade pip
"$APP_DIR/venv/bin/python" -m pip install --quiet -r "$APP_DIR/requirements.txt"

printf '#!/usr/bin/env bash\nexec "%s/venv/bin/python" "%s/aipalm_linux.py" "$@"\n' "$APP_DIR" "$APP_DIR" > "$BIN_DIR/ai-palm"
chmod +x "$BIN_DIR/ai-palm" "$APP_DIR/aipalm_linux.py"

printf '%s\n' \
  '[Desktop Entry]' \
  'Type=Application' \
  'Name=AI Palm' \
  'Name[ar]=نخلة AI' \
  'Comment=Explore, chat with, and share local AI models' \
  'Comment[ar]=استكشف نماذج الذكاء الاصطناعي المحلية وتحدث معها وشارك نموذجك' \
  "Exec=$BIN_DIR/ai-palm" \
  "Icon=$APP_DIR/ai-palm-mark.png" \
  'Terminal=false' \
  'Categories=Utility;Network;' \
  'StartupNotify=true' > "$DESKTOP_DIR/ai-palm.desktop"
chmod +x "$DESKTOP_DIR/ai-palm.desktop"

echo "AI Palm installed. Open it from the applications menu or run: ai-palm"

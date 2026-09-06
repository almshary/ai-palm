#!/usr/bin/env bash
set -euo pipefail

APP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/nawah-host"
BIN_DIR="$HOME/.local/bin"
DESKTOP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

command -v python3 >/dev/null || { echo "Python 3 is required."; exit 1; }
python3 -c 'import tkinter' 2>/dev/null || {
  echo "Tkinter is required. On Ubuntu/Debian run: sudo apt install python3-tk python3-venv"
  exit 1
}

mkdir -p "$APP_DIR" "$BIN_DIR" "$DESKTOP_DIR"
cp "$SCRIPT_DIR/nawah_host.py" "$SCRIPT_DIR/requirements.txt" "$SCRIPT_DIR/ai-palm-mark.png" "$APP_DIR/"
python3 -m venv "$APP_DIR/venv"
"$APP_DIR/venv/bin/python" -m pip install --quiet --upgrade pip
"$APP_DIR/venv/bin/python" -m pip install --quiet -r "$APP_DIR/requirements.txt"

printf '#!/usr/bin/env bash\nexec "%s/venv/bin/python" "%s/nawah_host.py" "$@"\n' "$APP_DIR" "$APP_DIR" > "$BIN_DIR/nawah-host"
chmod +x "$BIN_DIR/nawah-host" "$APP_DIR/nawah_host.py"

cat > "$DESKTOP_DIR/nawah-host.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=AI Palm Host
Name[ar]=نخلة AI للمضيف
Comment=Share a local AI model with the AI Palm network
Comment[ar]=شارك نموذج ذكاء اصطناعي محلياً مع شبكة نخلة AI
Exec=$BIN_DIR/nawah-host
Icon=$APP_DIR/ai-palm-mark.png
Terminal=false
Categories=Utility;Network;
StartupNotify=true
EOF
chmod +x "$DESKTOP_DIR/nawah-host.desktop"

echo "AI Palm Host installed. Open it from the applications menu or run: nawah-host"

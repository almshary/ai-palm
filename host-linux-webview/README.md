# AI Palm for Linux

This is the full AI Palm desktop client for Linux. It uses the same HTML, CSS, and JavaScript interface as the Windows WebView2 application and provides the same Explore, Chat, Share, Settings, English/Arabic, RTL/LTR, streaming reasoning, code, token usage, and context-meter features.

The Linux-native shell uses WebKitGTK through pywebview. The earlier Tkinter host remains in `host-linux/` as a compatibility fallback.

## Install on Ubuntu or Debian

Install the native WebKit requirements first:

```bash
sudo apt update
sudo apt install python3-venv python3-gi python3-gi-cairo gir1.2-gtk-3.0 gir1.2-webkit2-4.1
```

Then install AI Palm:

```bash
chmod +x install.sh uninstall.sh
./install.sh
```

Open **AI Palm** from the applications menu or run `ai-palm`.

## Source-tree run

```bash
python3 -m venv --system-site-packages .venv
.venv/bin/pip install -r requirements.txt
.venv/bin/python aipalm_linux.py
```

Settings are stored at `~/.config/ai-palm/settings.json` with owner-only permissions. The local engine API key is held only in memory and is never written to disk.

# AI Palm Host for Linux

Graphical Linux host for sharing a local model through the AI Palm volunteer network.

## Install on Ubuntu or Debian

Install the system requirements if needed:

```bash
sudo apt install python3 python3-tk python3-venv
```

Extract the release, open a terminal in its directory, then run:

```bash
chmod +x install.sh uninstall.sh
./install.sh
```

Open **AI Palm Host** from the applications menu. The application supports English and Arabic, detects NVIDIA through `nvidia-smi`, and detects AMD or Intel through Linux DRM/sysfs and `lspci`.

The local inference server must expose an OpenAI-compatible API. The model API key is kept only in memory and is never saved. Application settings are stored with user-only file permissions under `~/.config/nawah-host`.

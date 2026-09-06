# Contributing to AI Palm

Thank you for helping build a free volunteer network for local AI models.

## Before starting

- Open an issue for major features, protocol changes, or security-sensitive work.
- Never include API keys, platform keys, prompts, device records, or real `.env` files.
- Keep the host outbound-only: contributors must not require volunteers to expose an inbound port.
- Preserve compatibility with OpenAI-compatible `/v1/models` and `/v1/chat/completions` endpoints.
- Treat prompts and model outputs as untrusted content.

## Project areas

- `production/`: FastAPI coordinator, PostgreSQL models, Redis presence and streaming.
- `web/`: bilingual landing page and streaming chat interface.
- `host-win/`: native Windows host built with .NET Windows Forms.
- `host-linux/`: graphical Linux host built with Python and Tkinter.
- `tests/` and `production/tests/`: integration helpers and coordinator tests.

## Development checks

Run the coordinator tests:

```bash
python -m pytest production/tests/test_api.py -q
```

Validate the browser scripts:

```bash
node --check web/i18n.js
node --check web/landing.js
node --check web/app.js
```

Validate the Linux host:

```bash
python -m py_compile host-linux/nawah_host.py
python host-linux/nawah_host.py --self-test
```

Build the Windows host on Windows:

```powershell
dotnet build host-win/NawahHost.csproj
```

## Pull requests

- Keep each pull request focused on one outcome.
- Describe how the change was tested and which operating system/GPU was used.
- Add or update tests when behavior changes.
- Update both Arabic and English strings for user-facing changes.
- Do not commit generated binaries, databases, build folders, or secrets.

By participating, you agree to follow the project [Code of Conduct](CODE_OF_CONDUCT.md).

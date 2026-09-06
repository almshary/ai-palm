# AI Palm

<p align="center">
  <img src="web/assets/ai-palm-logo-en.png" alt="AI Palm" width="420">
</p>

<p align="center">
  <strong>Local compute, shared benefit.</strong><br>
  A volunteer network that connects local AI model hosts with people whose devices cannot run those models.
</p>

<p align="center">
  <a href="https://nawah.almshary.site">Live website</a> ·
  <a href="CONTRIBUTING.md">Contributing</a> ·
  <a href="SECURITY.md">Security</a> ·
  <a href="LICENSE">MIT License</a>
</p>

> [!IMPORTANT]
> AI Palm is currently a proof of concept. Prompts are processed on volunteer-owned computers. Never submit passwords, API keys, confidential documents, personal data, or other sensitive information.

## Overview

AI Palm lets a volunteer share access to a locally running, OpenAI-compatible language model without exposing the model server to the public internet. The host application makes outbound requests to a central coordinator, receives queued jobs, runs them against the local model, and streams the result back to the requesting browser.

The repository contains:

- A bilingual Arabic/English landing page and streaming chat interface.
- A zero-dependency Python coordinator for quick local demonstrations.
- A production FastAPI coordinator backed by PostgreSQL and Redis.
- A native Windows host application built with .NET Windows Forms.
- A graphical Linux host application built with Python, Tkinter, and a tray icon.
- Docker Compose and Caddy configuration for HTTPS deployment.
- Automated tests and GitHub Actions checks for the server, web code, and both host applications.

## How it works

```text
Browser
   │  submit prompt / receive streamed response
   ▼
AI Palm coordinator ─── PostgreSQL (devices and jobs)
   │                    Redis (presence, rate limits, live events)
   │ outbound polling
   ▼
Volunteer host application
   │ localhost / private network
   ▼
OpenAI-compatible model server
```

1. A host registers its device, GPU information, capabilities, and selected model.
2. The host sends heartbeats and polls the coordinator for work using outbound connections only.
3. A visitor selects an available model or a specific host and submits a prompt.
4. The coordinator atomically reserves the host, preventing a second job from using it concurrently.
5. The host sends the prompt to its configured OpenAI-compatible API.
6. Generated text and usage metrics are streamed back through the coordinator to the browser.
7. The host becomes available again when the job completes, fails, or times out.

## Features

- Live device availability and model filtering.
- Optional routing to a specific host.
- Atomic host reservation and automatic timeout recovery.
- Streaming responses with properly directed code blocks.
- Input, output, and total token reporting when supplied by the model engine, with estimates as a fallback.
- Automatic NVIDIA, AMD, and Intel GPU detection.
- Stable random installation IDs that do not depend on MAC addresses.
- English and Arabic user interfaces.
- System-tray operation on Windows and Linux.
- Outbound-only host networking; no router port forwarding is required.
- Model API keys remain on the host and are never sent to the coordinator.
- PostgreSQL persistence, Redis presence/events, public rate limiting, and automatic HTTPS in the production stack.

## Requirements

Choose the path that matches what you want to run.

| Component | Requirements |
| --- | --- |
| Local proof of concept | Python 3.10 or newer; no third-party packages |
| Production API development | Python 3.10+, dependencies in `production/requirements*.txt` |
| Production deployment | Docker Engine, Docker Compose, a Linux VPS, and a DNS name |
| Windows host development | Windows and the .NET 10 SDK |
| Linux host | Python 3, Tkinter, Pillow, and pystray |
| Browser checks | Node.js 22 or newer |
| Local inference | An OpenAI-compatible server such as LM Studio, vLLM, LocalAI, or Ollama |

## Quick start: local demo

The demo verifies registration, scheduling, streaming, and the web interface without running a real model.

1. Clone the repository and enter it:

   ```bash
   git clone https://github.com/almshary/ai-palm.git
   cd ai-palm
   ```

2. Start the local coordinator:

   ```bash
   python server.py
   ```

3. In a second terminal, start a simulated host:

   ```bash
   python worker.py --mode demo
   ```

4. Open [http://127.0.0.1:8765](http://127.0.0.1:8765), enter a prompt, and watch the response arrive.

The local coordinator stores registered device records in `data/hosts.json`; this runtime file is ignored by Git. The local job queue remains in memory.

## Connect a real local model

Start an inference server that implements these OpenAI-compatible endpoints:

- `GET /v1/models`
- `POST /v1/chat/completions`

Then run the command-line host, replacing the API URL and model name as needed:

```bash
python worker.py \
  --mode openai \
  --api-url http://127.0.0.1:1234/v1 \
  --model qwen2.5:7b
```

Use `--api-key` only if the local inference server requires one. The host talks to the model directly; the model API key is not included in coordinator requests.

## Host applications

### Windows

The native Windows application lets a volunteer configure the AI Palm coordinator URL, a platform registration key for closed testing, the local OpenAI-compatible API URL, an optional local model API key, and the model to share.

Select **Connect and start sharing** to make the device available. Closing the main window keeps the application running in the notification area; use **Exit** from the tray menu to stop sharing and close it completely.

Build a self-contained 64-bit executable from PowerShell:

```powershell
.\build_exe.ps1
```

The output is written to `dist\AIPalmHost.exe`. It includes the application icon and .NET runtime and does not require Python or a separate .NET installation on the destination computer.

To build the project without publishing a single-file executable:

```powershell
dotnet build .\host-win\NawahHost.csproj --configuration Release
```

Release builds are unsigned by default and may trigger Microsoft Defender SmartScreen reputation warnings. If you own a trusted code-signing certificate installed in the Windows certificate store, sign a completed build with:

```powershell
.\host-win\sign_exe.ps1 -CertificateThumbprint YOUR_40_CHARACTER_SHA1_THUMBPRINT
```

### Linux

On Ubuntu or Debian, install the system packages:

```bash
sudo apt update
sudo apt install python3 python3-tk python3-venv
```

Install the host application:

```bash
cd host-linux
chmod +x install.sh uninstall.sh
./install.sh
```

Launch **AI Palm Host** from the applications menu. Settings are stored with user-only permissions under `~/.config/nawah-host`. The model API key is held in memory and is not saved.

To remove the Linux application:

```bash
cd host-linux
./uninstall.sh
```

## Production API development

Create and activate a virtual environment, then install the server and test dependencies:

```bash
python -m venv .venv
```

On Linux or macOS:

```bash
source .venv/bin/activate
pip install -r production/requirements.txt -r production/requirements-dev.txt
```

On Windows PowerShell:

```powershell
.\.venv\Scripts\Activate.ps1
pip install -r production\requirements.txt -r production\requirements-dev.txt
```

The development defaults use SQLite and an in-memory realtime backend, so no external services are required:

```bash
uvicorn production.app.main:app --reload
```

Open [http://127.0.0.1:8000](http://127.0.0.1:8000). The health endpoint is available at `/api/status`.

## Production deployment with Docker

The production stack runs the API, PostgreSQL, Redis, and Caddy. Only ports 80 and 443 are exposed publicly.

### 1. Prepare the server

- Use a recent Ubuntu LTS VPS with at least 2 GB RAM for an evaluation deployment.
- Install Docker Engine and Docker Compose.
- Point an A/AAAA record for your domain to the server.
- Allow inbound SSH, HTTP, and HTTPS traffic on ports 22, 80, and 443.

### 2. Configure secrets

From the repository root:

```bash
cd production
cp .env.example .env
```

Generate three different secrets:

```bash
openssl rand -hex 32
openssl rand -hex 32
openssl rand -hex 32
```

Edit `.env` and set:

- `NAWAH_DOMAIN` to the hostname without `https://`.
- `POSTGRES_PASSWORD` and the matching password inside `NAWAH_DATABASE_URL`.
- `REDIS_PASSWORD` and the matching password inside `NAWAH_REDIS_URL`.
- `NAWAH_REGISTRATION_KEY` to the third secret.
- `NAWAH_ALLOWED_HOSTS` and `NAWAH_PUBLIC_BASE_URL` to the public hostname.

Never commit `.env`. It is already excluded by `.gitignore`.

### 3. Start the stack

```bash
docker compose up -d --build
```

Database migrations run before the API starts. Once DNS resolves correctly, Caddy obtains and renews the TLS certificate automatically.

### 4. Verify the deployment

```bash
docker compose ps
curl https://your-domain.example/api/status
docker compose logs --tail=100 api caddy
```

A healthy API returns JSON containing `"ok": true`. Enter the public coordinator URL and `NAWAH_REGISTRATION_KEY` in each host application.

### Updating

Pull or upload the new release, then rebuild the services:

```bash
docker compose up -d --build
```

### Backing up PostgreSQL

```bash
mkdir -p backups
docker compose exec -T postgres sh -c 'pg_dump -U "$POSTGRES_USER" "$POSTGRES_DB"' \
  | gzip > "backups/nawah-$(date +%F-%H%M).sql.gz"
```

See [`production/DEPLOY.md`](production/DEPLOY.md) for the existing VPS and aaPanel deployment notes.

## Configuration reference

Production application settings use the `NAWAH_` prefix.

| Variable | Default | Purpose |
| --- | --- | --- |
| `NAWAH_ENVIRONMENT` | `development` | Runtime environment label |
| `NAWAH_DATABASE_URL` | SQLite development database | SQLAlchemy async database URL |
| `NAWAH_REDIS_URL` | `memory://` | Redis URL, or in-memory mode for development/tests |
| `NAWAH_REGISTRATION_KEY` | Development-only value | Shared key used to authorize host registration |
| `NAWAH_ALLOWED_HOSTS` | Localhost values | Comma-separated HTTP Host allowlist |
| `NAWAH_PUBLIC_BASE_URL` | `http://127.0.0.1:8000` | Public base URL advertised by the service |
| `NAWAH_HOST_TIMEOUT_SECONDS` | `35` | Time before a missing host is treated as offline |
| `NAWAH_JOB_TIMEOUT_SECONDS` | `90` | Maximum time before an unfinished job expires |
| `NAWAH_PUBLIC_JOBS_PER_MINUTE` | `12` | Public job submission rate limit per client |
| `NAWAH_AUTO_CREATE_SCHEMA` | `true` | Create tables automatically in development; production uses Alembic |
| `NAWAH_MAX_RESULT_CHARS` | `100000` | Maximum stored result length |

## API overview

The host protocol and web interface currently use these endpoints:

| Method | Endpoint | Purpose |
| --- | --- | --- |
| `GET` | `/api/status` | Service, database, and realtime health |
| `GET` | `/api/hosts` | List currently available hosts |
| `POST` | `/api/hosts/register` | Register or refresh a host identity |
| `POST` | `/api/hosts/{host_id}/heartbeat` | Update host presence or availability |
| `GET` | `/api/hosts/{host_id}/jobs/next` | Poll for the next assigned job |
| `POST` | `/api/jobs` | Submit a chat or image job |
| `GET` | `/api/jobs/{job_id}` | Read the current job state |
| `POST` | `/api/jobs/{job_id}/stream` | Append streamed output from the host |
| `POST` | `/api/jobs/{job_id}/complete` | Complete a job and attach usage data |
| `POST` | `/api/jobs/{job_id}/fail` | Mark a job as failed |
| `GET` | `/api/jobs/{job_id}/events` | Receive Server-Sent Events for live updates |

Host-only endpoints require the device token returned during registration. Request and response models reject unknown fields and enforce size limits.

## Testing and validation

Install the production development requirements, then run:

```bash
python -m pytest production/tests/test_api.py -q
python -m py_compile host-linux/nawah_host.py
python host-linux/nawah_host.py --self-test
node --check web/i18n.js
node --check web/landing.js
node --check web/app.js
```

On Windows, also run:

```powershell
dotnet build .\host-win\NawahHost.csproj --configuration Release
```

GitHub Actions runs these checks on every push to `main` and on every pull request.

## Project structure

```text
.
├── .github/                 Issue templates and CI workflow
├── host-linux/              Python/Tkinter Linux host
├── host-win/                Native .NET Windows host
├── production/
│   ├── app/                 FastAPI application, models, schemas, realtime layer
│   ├── migrations/          Alembic database migrations
│   ├── tests/               Production API tests
│   ├── compose.yaml         PostgreSQL, Redis, API, and Caddy stack
│   └── Dockerfile           Production API image
├── tests/                   Local mock OpenAI-compatible server
├── web/                     Landing page, chat UI, translations, and assets
├── server.py                Standard-library local coordinator
├── worker.py                Command-line demo/OpenAI-compatible host
└── build_exe.ps1            Windows single-file build script
```

## Security and privacy model

- Treat every prompt and model response as untrusted content.
- Do not send sensitive or confidential material through the volunteer network.
- The local inference API should remain bound to localhost or a trusted private network.
- Hosts make outbound requests; do not expose local inference ports through a router or public firewall.
- A model API key is used only between the host application and its configured inference server.
- The platform registration key is separate from the model API key.
- The Windows application encrypts the saved platform key for the current Windows account; the model key is not persisted.
- The shared registration key is suitable for a closed proof of concept, not a large public service.

Please report vulnerabilities privately as described in [`SECURITY.md`](SECURITY.md).

## Current limitations

- There are no end-user accounts or host-owner accounts yet.
- The shared host registration key is an interim closed-test mechanism.
- Content moderation, abuse reporting, and comprehensive operator monitoring are not implemented.
- The lightweight local coordinator persists hosts but keeps jobs in memory.
- The host applications currently execute chat jobs; image capability is reserved for a later stage.
- Usage counts may be estimated when an inference engine does not return token usage.
- Windows release binaries require a trusted code-signing certificate to avoid unknown-publisher warnings.

Before a broad public launch, add individual host accounts, one-time device pairing, stronger abuse controls, user-facing privacy/terms pages, operational alerting, and a formal retention policy.

## Contributing

Contributions are welcome across the coordinator, web interface, Windows and Linux hosts, security, translations, and NVIDIA/AMD/Intel compatibility.

Read [`CONTRIBUTING.md`](CONTRIBUTING.md) before opening a pull request. For substantial architecture or protocol changes, open an issue first. By participating, you agree to follow the [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md).

## License

AI Palm is open-source software released under the [MIT License](LICENSE). You may use, copy, modify, merge, publish, distribute, sublicense, and sell copies of the software, provided that the copyright and license notices are preserved.

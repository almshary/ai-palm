#!/usr/bin/env python3
"""A dependency-free coordinator and web server for the AI Palm proof of concept."""

from __future__ import annotations

import argparse
import hashlib
import json
import mimetypes
import os
import re
import secrets
import threading
import time
import uuid
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse


ROOT = Path(__file__).resolve().parent
HOSTS_FILE = Path(os.environ.get("NAWAH_HOSTS_FILE", ROOT / "data" / "hosts.json"))
HOST_TIMEOUT_SECONDS = 15
JOB_TIMEOUT_SECONDS = 45
HOST_ID_PATTERN = re.compile(r"^[A-Za-z0-9_-]{8,64}$")


class State:
    def __init__(self) -> None:
        self.lock = threading.RLock()
        self.hosts: dict[str, dict] = self.load_hosts()
        self.jobs: dict[str, dict] = {}

    def load_hosts(self) -> dict[str, dict]:
        try:
            payload = json.loads(HOSTS_FILE.read_text(encoding="utf-8"))
            items = payload.get("hosts", []) if isinstance(payload, dict) else []
        except (OSError, json.JSONDecodeError):
            return {}
        hosts: dict[str, dict] = {}
        for item in items:
            if not isinstance(item, dict):
                continue
            host_id = str(item.get("id") or "")
            if not HOST_ID_PATTERN.fullmatch(host_id):
                continue
            host = dict(item)
            host.update(online=False, busy=False, last_seen=0)
            hosts[host_id] = host
        return hosts

    def save_hosts(self) -> None:
        try:
            HOSTS_FILE.parent.mkdir(parents=True, exist_ok=True)
            payload = {"hosts": list(self.hosts.values())}
            temporary = HOSTS_FILE.with_suffix(HOSTS_FILE.suffix + ".tmp")
            temporary.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
            temporary.replace(HOSTS_FILE)
        except OSError as exc:
            print(f"تعذر حفظ سجل الأجهزة: {exc}")

    def maintenance(self) -> None:
        now = time.time()
        with self.lock:
            for host in self.hosts.values():
                host["online"] = now - host["last_seen"] <= HOST_TIMEOUT_SECONDS

            for job in self.jobs.values():
                lease_time = job.get("lease_updated_at") or job.get("started_at") or now
                expired_target = job["status"] == "queued" and job.get("target_host_id") and now - job["created_at"] > JOB_TIMEOUT_SECONDS
                expired_running = job["status"] == "running" and now - lease_time > JOB_TIMEOUT_SECONDS
                if expired_target or expired_running:
                    host_id = job.get("host_id") or job.get("target_host_id")
                    targeted = bool(job.get("target_host_id"))
                    job.update(
                        status="failed" if targeted else "queued",
                        host_id=None,
                        started_at=None,
                        lease_updated_at=None,
                        error="انتهت مهلة الجهاز المحدد" if targeted else None,
                        completed_at=now if targeted else None,
                        note="تعذر إكمال المهمة على الجهاز المحدد" if targeted else "أعيدت المهمة للطابور بعد انقطاع المضيف",
                    )
                    if host_id in self.hosts:
                        self.hosts[host_id]["busy"] = False


STATE = State()


def public_host(host: dict) -> dict:
    return {key: value for key, value in host.items() if key not in {"token", "token_hash"}}


def public_job(job: dict) -> dict:
    return dict(job)


class Handler(BaseHTTPRequestHandler):
    server_version = "AIPalmPOC/0.7"

    def log_message(self, fmt: str, *args) -> None:
        print(f"[{self.log_date_time_string()}] {fmt % args}")

    def send_json(self, payload: object, status: int = HTTPStatus.OK) -> None:
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def read_json(self) -> dict:
        length = int(self.headers.get("Content-Length", "0"))
        if length > 32_000:
            raise ValueError("الطلب أكبر من الحد المسموح")
        raw = self.rfile.read(length)
        return json.loads(raw or b"{}")

    def authenticate_host(self, host_id: str, token: str | None) -> dict | None:
        host = STATE.hosts.get(host_id)
        if not host or not token:
            return None
        provided_hash = hashlib.sha256(token.encode("utf-8")).hexdigest()
        if not secrets.compare_digest(str(host.get("token_hash") or ""), provided_hash):
            return None
        return host

    def do_GET(self) -> None:  # noqa: N802
        parsed = urlparse(self.path)
        path = parsed.path
        STATE.maintenance()

        if path == "/api/status":
            self.send_json({"ok": True, "version": "0.7.0"})
            return

        if path == "/api/hosts":
            with STATE.lock:
                connected = [item for item in STATE.hosts.values() if item.get("online") and item.get("enabled")]
                disconnected = [item for item in STATE.hosts.values() if not item.get("online") or not item.get("enabled")]
                hosts = [public_host(item) for item in connected]
                offline_hosts = [public_host(item) for item in disconnected]
                registered_count = len(STATE.hosts)
                model_groups: dict[str, dict] = {}
                for host in connected:
                    model_name = str(host.get("model") or "غير محدد")
                    group = model_groups.setdefault(model_name, {"model": model_name, "connected": 0, "available": 0})
                    group["connected"] += 1
                    if not host.get("busy"):
                        group["available"] += 1
            hosts.sort(key=lambda item: item["name"])
            offline_hosts.sort(key=lambda item: item["name"])
            models = sorted(model_groups.values(), key=lambda item: (-item["available"], item["model"].lower()))
            connected_count = len(hosts)
            self.send_json({
                "hosts": hosts,
                "offline_hosts": offline_hosts,
                "models": models,
                "stats": {
                    "registered": registered_count,
                    "connected": connected_count,
                    "offline": registered_count - connected_count,
                },
            })
            return

        if path == "/api/jobs":
            with STATE.lock:
                jobs = [public_job(item) for item in STATE.jobs.values()]
            jobs.sort(key=lambda item: item["created_at"], reverse=True)
            self.send_json({"jobs": jobs[:25]})
            return

        if path.startswith("/api/jobs/"):
            job_id = path.rsplit("/", 1)[-1]
            with STATE.lock:
                job = STATE.jobs.get(job_id)
            if not job:
                self.send_json({"error": "المهمة غير موجودة"}, HTTPStatus.NOT_FOUND)
            else:
                self.send_json({"job": public_job(job)})
            return

        parts = path.strip("/").split("/")
        if len(parts) == 5 and parts[:2] == ["api", "hosts"] and parts[3:] == ["jobs", "next"]:
            host_id = parts[2]
            token = self.headers.get("X-Host-Token") or parse_qs(parsed.query).get("token", [None])[0]
            with STATE.lock:
                host = self.authenticate_host(host_id, token)
                if not host:
                    self.send_json({"error": "بيانات المضيف غير صحيحة"}, HTTPStatus.UNAUTHORIZED)
                    return
                host["last_seen"] = time.time()
                host["online"] = True
                if not host["enabled"]:
                    self.send_json({"job": None})
                    return
                job = next(
                    (
                        item
                        for item in sorted(STATE.jobs.values(), key=lambda value: value["created_at"])
                        if item["status"] == "queued"
                        and item.get("target_host_id") == host_id
                    ),
                    None,
                )
                if not job and host["busy"]:
                    self.send_json({"job": None})
                    return
                if not job:
                    job = next(
                    (
                        item
                        for item in sorted(STATE.jobs.values(), key=lambda value: value["created_at"])
                        if item["status"] == "queued"
                        and not item.get("target_host_id")
                        and item["kind"] in host["capabilities"]
                        and (not item.get("model") or item["model"] == host.get("model"))
                    ),
                    None,
                )
                if job:
                    job.update(status="running", host_id=host_id, started_at=time.time(), lease_updated_at=time.time(), note="تُنفذ الآن على جهاز متطوع")
                    host["busy"] = True
            self.send_json({"job": public_job(job) if job else None})
            return

        self.serve_static(path)

    def do_POST(self) -> None:  # noqa: N802
        path = urlparse(self.path).path
        try:
            body = self.read_json()
        except (ValueError, json.JSONDecodeError) as exc:
            self.send_json({"error": str(exc)}, HTTPStatus.BAD_REQUEST)
            return

        if path == "/api/hosts/register":
            requested_host_id = str(body.get("host_id") or "")
            host_id = requested_host_id if HOST_ID_PATTERN.fullmatch(requested_host_id) else uuid.uuid4().hex
            token = secrets.token_urlsafe(24)
            capabilities = [item for item in body.get("capabilities", ["chat"]) if item in {"chat", "image"}]
            if not capabilities:
                capabilities = ["chat"]
            with STATE.lock:
                existing = STATE.hosts.get(host_id, {})
                STATE.hosts[host_id] = {
                    "id": host_id,
                    "token_hash": hashlib.sha256(token.encode("utf-8")).hexdigest(),
                    "name": str(body.get("name") or "جهاز متطوع")[:80],
                    "gpu": str(body.get("gpu") or "بطاقة غير معروفة")[:120],
                    "gpu_vendor": str(body.get("gpu_vendor") or "غير معروف")[:40],
                    "vram_gb": body.get("vram_gb"),
                    "mode": str(body.get("mode") or "demo")[:30],
                    "model": str(body.get("model") or "غير محدد")[:120],
                    "capabilities": capabilities,
                    "enabled": bool(body.get("enabled", True)),
                    "busy": bool(existing.get("busy", False)),
                    "online": True,
                    "last_seen": time.time(),
                    "registered_at": existing.get("registered_at", time.time()),
                }
                STATE.save_hosts()
            self.send_json({"host_id": host_id, "token": token}, HTTPStatus.CREATED)
            return

        if path == "/api/jobs":
            prompt = str(body.get("prompt") or "").strip()
            kind = str(body.get("kind") or "chat")
            requested_model = str(body.get("model") or "").strip()[:120]
            target_host_id = str(body.get("target_host_id") or "").strip()
            if kind not in {"chat", "image"}:
                self.send_json({"error": "نوع المهمة غير مدعوم"}, HTTPStatus.BAD_REQUEST)
                return
            if not prompt or len(prompt) > 2_000:
                self.send_json({"error": "اكتب طلبًا بين 1 و2000 حرف"}, HTTPStatus.BAD_REQUEST)
                return
            STATE.maintenance()
            with STATE.lock:
                if target_host_id and not HOST_ID_PATTERN.fullmatch(target_host_id):
                    self.send_json({"error": "معرّف الجهاز المحدد غير صحيح"}, HTTPStatus.UNPROCESSABLE_ENTITY)
                    return
                if target_host_id:
                    target = STATE.hosts.get(target_host_id)
                    available_hosts = [target] if target and target["online"] and target["enabled"] and not target["busy"] and kind in target["capabilities"] and (not requested_model or target.get("model") == requested_model) else []
                else:
                    available_hosts = [
                        host
                        for host in STATE.hosts.values()
                        if host["online"]
                        and host["enabled"]
                        and not host["busy"]
                        and kind in host["capabilities"]
                        and (not requested_model or host.get("model") == requested_model)
                    ]
                if not available_hosts:
                    error = "الجهاز المحدد قيد الاستخدام أو لم يعد متاحًا" if target_host_id else ("النموذج المحدد غير متاح حاليًا" if requested_model else "لا يوجد جهاز متاح لهذه المهمة حاليًا")
                    self.send_json({"error": error}, HTTPStatus.CONFLICT if target_host_id else HTTPStatus.SERVICE_UNAVAILABLE)
                    return
                if target_host_id:
                    available_hosts[0]["busy"] = True
                    requested_model = str(available_hosts[0].get("model") or requested_model)
                job_id = uuid.uuid4().hex[:12]
                job = {
                    "id": job_id,
                    "kind": kind,
                    "model": requested_model or None,
                    "prompt": prompt,
                    "status": "queued",
                    "host_id": None,
                    "target_host_id": target_host_id or None,
                    "result": "",
                    "error": None,
                    "usage": None,
                    "note": "محجوز للجهاز المحدد" if target_host_id else "بانتظار جهاز متاح",
                    "created_at": time.time(),
                    "started_at": None,
                    "lease_updated_at": None,
                    "completed_at": None,
                }
                STATE.jobs[job_id] = job
            self.send_json({"job": public_job(job)}, HTTPStatus.CREATED)
            return

        parts = path.strip("/").split("/")
        if len(parts) == 4 and parts[:2] == ["api", "hosts"] and parts[3] == "heartbeat":
            host_id = parts[2]
            token = self.headers.get("X-Host-Token")
            with STATE.lock:
                host = self.authenticate_host(host_id, token)
                if not host:
                    self.send_json({"error": "بيانات المضيف غير صحيحة"}, HTTPStatus.UNAUTHORIZED)
                    return
                host["last_seen"] = time.time()
                host["online"] = True
                enabled_changed = False
                if "enabled" in body:
                    requested_enabled = bool(body["enabled"])
                    enabled_changed = host.get("enabled") != requested_enabled
                    host["enabled"] = requested_enabled
                if host["enabled"]:
                    for job in STATE.jobs.values():
                        if job["status"] == "running" and job.get("host_id") == host_id:
                            job["lease_updated_at"] = time.time()
                else:
                    for job in STATE.jobs.values():
                        targeted = job.get("target_host_id") == host_id and job["status"] in {"queued", "running"}
                        generic_running = job["status"] == "running" and job.get("host_id") == host_id and not job.get("target_host_id")
                        if targeted:
                            job.update(status="failed", host_id=None, started_at=None, lease_updated_at=None, error="انقطع الجهاز المحدد أو أوقف المشاركة", completed_at=time.time(), note="تعذر إكمال المهمة على الجهاز المحدد")
                        elif generic_running:
                            job.update(status="queued", host_id=None, started_at=None, lease_updated_at=None, note="أعيدت المهمة للطابور بعد إيقاف المضيف")
                    host["busy"] = False
                if enabled_changed:
                    STATE.save_hosts()
            self.send_json({"ok": True})
            return

        if len(parts) == 4 and parts[:2] == ["api", "jobs"] and parts[3] == "stream":
            job_id = parts[2]
            token = self.headers.get("X-Host-Token")
            with STATE.lock:
                job = STATE.jobs.get(job_id)
                host = self.authenticate_host(job.get("host_id") if job else "", token)
                if not job or not host or job["status"] != "running":
                    self.send_json({"error": "المهمة أو بيانات المضيف غير صحيحة"}, HTTPStatus.UNAUTHORIZED)
                    return
                delta = str(body.get("delta") or "")
                if delta:
                    job["result"] = (job.get("result") or "") + delta
                    job["result"] = job["result"][:100_000]
                    job["note"] = "تصل الإجابة الآن"
                usage = body.get("usage")
                if isinstance(usage, dict):
                    job["usage"] = usage
                job["lease_updated_at"] = time.time()
            self.send_json({"ok": True})
            return

        if len(parts) == 4 and parts[:2] == ["api", "jobs"] and parts[3] in {"complete", "fail"}:
            job_id = parts[2]
            token = self.headers.get("X-Host-Token")
            with STATE.lock:
                job = STATE.jobs.get(job_id)
                host = self.authenticate_host(job.get("host_id") if job else "", token)
                if not job or not host:
                    self.send_json({"error": "المهمة أو بيانات المضيف غير صحيحة"}, HTTPStatus.UNAUTHORIZED)
                    return
                if parts[3] == "complete":
                    job.update(
                        status="completed",
                        result=str(body.get("result") or job.get("result") or "")[:100_000],
                        completed_at=time.time(),
                        note="اكتملت المهمة بنجاح",
                    )
                    if isinstance(body.get("usage"), dict):
                        job["usage"] = body["usage"]
                else:
                    job.update(
                        status="failed",
                        error=str(body.get("error") or "فشل التنفيذ")[:1_000],
                        completed_at=time.time(),
                        note="تعذر إكمال المهمة",
                    )
                host["busy"] = False
            self.send_json({"ok": True})
            return

        self.send_json({"error": "المسار غير موجود"}, HTTPStatus.NOT_FOUND)

    def serve_static(self, path: str) -> None:
        relative = "index.html" if path in {"", "/"} else ("chat/index.html" if path in {"/chat", "/chat/"} else path.lstrip("/"))
        target = (ROOT / "web" / relative).resolve()
        web_root = (ROOT / "web").resolve()
        if web_root not in target.parents and target != web_root:
            self.send_error(HTTPStatus.FORBIDDEN)
            return
        if target.is_dir():
            target = target / "index.html"
        if not target.is_file():
            self.send_error(HTTPStatus.NOT_FOUND)
            return
        data = target.read_bytes()
        content_type = mimetypes.guess_type(target.name)[0] or "application/octet-stream"
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", f"{content_type}; charset=utf-8" if content_type.startswith("text/") else content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)


def main() -> None:
    parser = argparse.ArgumentParser(description="AI Palm proof-of-concept coordinator")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"AI Palm is ready at http://{args.host}:{args.port}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()

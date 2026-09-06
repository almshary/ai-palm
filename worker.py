#!/usr/bin/env python3
"""Volunteer host client for the AI Palm proof of concept."""

from __future__ import annotations

import argparse
import json
import socket
import subprocess
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path


def request_json(url: str, method: str = "GET", body: dict | None = None, token: str | None = None, api_key: str | None = None) -> dict:
    headers = {"Accept": "application/json"}
    data = None
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"
    if token:
        headers["X-Host-Token"] = token
    if api_key:
        headers["Authorization"] = f"Bearer {api_key}"
    request = urllib.request.Request(url, data=data, headers=headers, method=method)
    with urllib.request.urlopen(request, timeout=45) as response:
        return json.loads(response.read().decode("utf-8"))


def detect_gpu() -> tuple[str, float | None]:
    try:
        result = subprocess.run(
            ["nvidia-smi", "--query-gpu=name,memory.total", "--format=csv,noheader,nounits"],
            capture_output=True,
            text=True,
            timeout=5,
            check=True,
        )
        first_line = result.stdout.strip().splitlines()[0]
        name, memory_mb = [item.strip() for item in first_line.rsplit(",", 1)]
        return name, round(float(memory_mb) / 1024, 1)
    except (FileNotFoundError, subprocess.SubprocessError, ValueError, IndexError):
        return "GPU تجريبي", None


def detect_vendor(gpu_name: str) -> str:
    lowered = gpu_name.lower()
    if any(value in lowered for value in ("nvidia", "geforce", "quadro")):
        return "NVIDIA"
    if "amd" in lowered or "radeon" in lowered:
        return "AMD"
    if any(value in lowered for value in ("intel", "arc", "iris")):
        return "Intel"
    return "غير معروف"


def get_or_create_device_id() -> str:
    path = Path.home() / ".nawah-host" / "device-id"
    try:
        existing = path.read_text(encoding="utf-8").strip()
        if existing:
            return existing
    except OSError:
        pass
    device_id = uuid.uuid4().hex
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(device_id, encoding="utf-8")
    except OSError:
        pass
    return device_id


def demo_response(prompt: str, gpu: str) -> str:
    time.sleep(1.2)
    return (
        "هذه إجابة تجريبية نُفذت عبر جهاز المضيف بنجاح.\n\n"
        f"الطلب المستلم: {prompt}\n\n"
        f"الجهاز المنفذ: {gpu}\n"
        "عند الانتقال للاختبار الفعلي يمكن تشغيل نفس المهمة عبر أي خادم متوافق مع OpenAI API."
    )


def openai_compatible_response(prompt: str, model: str, api_url: str, api_key: str) -> str:
    payload = {
        "model": model,
        "stream": False,
        "messages": [{"role": "user", "content": prompt}],
    }
    result = request_json(f"{api_url.rstrip('/')}/chat/completions", "POST", payload, api_key=api_key)
    choices = result.get("choices") or []
    return str(choices[0].get("message", {}).get("content") if choices else "لم يُرجع النموذج نصًا")


def main() -> None:
    parser = argparse.ArgumentParser(description="Share this computer with AI Palm")
    parser.add_argument("--server", default="http://127.0.0.1:8765")
    parser.add_argument("--name", default=socket.gethostname())
    parser.add_argument("--mode", choices=["demo", "openai"], default="demo")
    parser.add_argument("--model", default="qwen2.5:7b")
    parser.add_argument("--api-url", default="http://127.0.0.1:1234/v1")
    parser.add_argument("--api-key", default="", help="Optional Bearer API key; prefer NAWAH_MODEL_API_KEY")
    parser.add_argument("--once", action="store_true", help="Exit after completing one job")
    args = parser.parse_args()

    base_url = args.server.rstrip("/")
    device_id = get_or_create_device_id()
    gpu, vram_gb = detect_gpu()
    gpu_vendor = detect_vendor(gpu)
    registration = request_json(
        f"{base_url}/api/hosts/register",
        "POST",
        {
            "host_id": device_id,
            "name": args.name,
            "gpu": gpu,
            "gpu_vendor": gpu_vendor,
            "vram_gb": vram_gb,
            "mode": args.mode,
            "model": args.model if args.mode == "openai" else "demo-model",
            "capabilities": ["chat"],
            "enabled": True,
        },
    )
    host_id = registration["host_id"]
    token = registration["token"]
    print(f"المشاركة مفعلة — {gpu} — المعرّف {host_id}")

    try:
        while True:
            request_json(f"{base_url}/api/hosts/{host_id}/heartbeat", "POST", {"enabled": True}, token)
            response = request_json(f"{base_url}/api/hosts/{host_id}/jobs/next", token=token)
            job = response.get("job")
            if not job:
                time.sleep(1.5)
                continue
            print(f"تنفيذ المهمة {job['id']}...")
            try:
                if args.mode == "openai":
                    import os
                    api_key = os.environ.get("NAWAH_MODEL_API_KEY", args.api_key)
                    result = openai_compatible_response(job["prompt"], args.model, args.api_url, api_key)
                else:
                    result = demo_response(job["prompt"], gpu)
                request_json(f"{base_url}/api/jobs/{job['id']}/complete", "POST", {"result": result}, token)
                print("اكتملت المهمة")
            except Exception as exc:  # keep the volunteer client alive after model errors
                request_json(f"{base_url}/api/jobs/{job['id']}/fail", "POST", {"error": str(exc)}, token)
                print(f"فشلت المهمة: {exc}")
            if args.once:
                break
    except KeyboardInterrupt:
        print("\nتم إيقاف المشاركة")
    except urllib.error.URLError as exc:
        print(f"تعذر الاتصال بالمنصة: {exc}")


if __name__ == "__main__":
    main()

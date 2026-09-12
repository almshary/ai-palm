#!/usr/bin/env python3
"""AI Palm desktop client for Linux using the shared Windows/Linux web UI."""

from __future__ import annotations

import argparse
import json
import os
import shutil
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any


APP_VERSION = "0.1.7"
OFFICIAL_COORDINATOR = "https://nawah.almshary.site"
CONFIG_DIR = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "ai-palm"
CONFIG_PATH = Path(os.environ.get("AI_PALM_SETTINGS_PATH", CONFIG_DIR / "settings.json"))


@dataclass(frozen=True)
class GpuInfo:
    vendor: str = "Unknown"
    name: str = "No GPU detected"
    vram_gb: int | None = None


@dataclass(frozen=True)
class EngineInfo:
    kind: str = "Unknown"
    name: str = "OpenAI-compatible"
    version: str = ""
    root: str = ""
    context_length: int | None = None


def run_command(args: list[str]) -> str:
    try:
        return subprocess.run(args, capture_output=True, text=True, timeout=5, check=False).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return ""


def detect_gpu() -> GpuInfo:
    nvidia = run_command(["nvidia-smi", "--query-gpu=name,memory.total", "--format=csv,noheader,nounits"])
    if nvidia:
        name, _, memory = nvidia.splitlines()[0].partition(",")
        try:
            vram = round(float(memory.strip()) / 1024)
        except ValueError:
            vram = None
        return GpuInfo("NVIDIA", name.strip(), vram)

    cards = sorted(Path("/sys/class/drm").glob("card[0-9]*/device"))
    pci = run_command(["lspci", "-mm"])
    for card in cards:
        try:
            vendor_id = (card / "vendor").read_text().strip().lower()
        except OSError:
            continue
        vendor = {"0x1002": "AMD", "0x10de": "NVIDIA", "0x8086": "Intel"}.get(vendor_id, "Unknown")
        line = next((item for item in pci.splitlines() if ("VGA" in item or "3D controller" in item) and vendor.lower() in item.lower()), "")
        quoted = [part.strip() for part in line.split('"') if part.strip()]
        name = quoted[-2] if len(quoted) >= 2 else f"{vendor} GPU"
        try:
            vram = round(int((card / "mem_info_vram_total").read_text().strip()) / (1024**3))
        except (OSError, ValueError):
            vram = None
        return GpuInfo(vendor, name, vram)

    for line in pci.splitlines():
        if "VGA" not in line and "3D controller" not in line:
            continue
        lower = line.lower()
        vendor = "AMD" if "amd" in lower or "ati" in lower else "NVIDIA" if "nvidia" in lower else "Intel" if "intel" in lower else "Unknown"
        parts = [part.strip() for part in line.split('"') if part.strip()]
        return GpuInfo(vendor, parts[-2] if len(parts) >= 2 else line, None)
    return GpuInfo()


def default_settings() -> dict[str, Any]:
    return {
        "device_id": uuid.uuid4().hex,
        "device_name": socket.gethostname(),
        "server_url": OFFICIAL_COORDINATOR,
        "platform_key": "",
        "api_url": "http://127.0.0.1:1234/v1",
        "model": "",
        "language": "en",
        "mode": "openai",
    }


def load_settings() -> dict[str, Any]:
    settings = default_settings()
    legacy_path = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "nawah-host" / "settings.json"
    try:
        source = CONFIG_PATH if CONFIG_PATH.is_file() else legacy_path
        saved = json.loads(source.read_text(encoding="utf-8"))
        settings.update({key: value for key, value in saved.items() if key in settings})
    except (OSError, ValueError, TypeError):
        pass
    settings["server_url"] = OFFICIAL_COORDINATOR
    return settings


def save_settings(settings: dict[str, Any]) -> None:
    CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
    safe = {key: value for key, value in settings.items() if key != "api_key"}
    CONFIG_PATH.write_text(json.dumps(safe, indent=2, ensure_ascii=False), encoding="utf-8")
    CONFIG_PATH.chmod(0o600)


def api_endpoint(base: str, endpoint: str) -> str:
    return f"{base.strip().rstrip('/')}/{endpoint.lstrip('/')}"


def normalize_engine_root(value: str) -> str:
    value = value.strip().rstrip("/")
    for suffix in ("/v1", "/api/v1", "/api/v0"):
        if value.lower().endswith(suffix):
            return value[: -len(suffix)]
    return value


def request_json(url: str, *, method: str = "GET", body: Any = None, headers: dict[str, str] | None = None, timeout: int = 20) -> dict[str, Any]:
    payload = None if body is None else json.dumps(body).encode("utf-8")
    request_headers = {"Accept": "application/json", **(headers or {})}
    if payload is not None:
        request_headers["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data=payload, headers=request_headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            raw = response.read().decode("utf-8")
            return json.loads(raw) if raw else {"ok": True}
    except urllib.error.HTTPError as error:
        raw = error.read().decode("utf-8", "replace")
        try:
            payload_error = json.loads(raw)
            detail = payload_error.get("detail") or payload_error.get("error") or payload_error.get("message")
        except (ValueError, TypeError):
            detail = raw.strip()
        raise RuntimeError(detail or f"HTTP {error.code}") from error
    except (urllib.error.URLError, TimeoutError) as error:
        raise RuntimeError(str(getattr(error, "reason", error))) from error


def try_json(url: str, headers: dict[str, str]) -> dict[str, Any] | None:
    try:
        return request_json(url, headers=headers, timeout=3)
    except Exception:
        return None


def walk_json(value: Any):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from walk_json(child)
    elif isinstance(value, list):
        for child in value:
            yield from walk_json(child)


def find_context_length(payload: Any, model: str) -> int | None:
    keys = {"context_length", "max_context_length", "context_window", "context_size", "n_ctx", "n_ctx_train", "num_ctx"}
    objects = list(walk_json(payload))
    matching = [item for item in objects if any(str(item.get(key, "")).lower() == model.lower() for key in ("id", "model", "key", "name"))]
    for item in matching + [item for item in objects if item not in matching]:
        for key, value in item.items():
            if str(key).lower() not in keys and not str(key).lower().endswith(".context_length"):
                continue
            try:
                length = int(value)
                if length > 0:
                    return length
            except (TypeError, ValueError):
                pass
    return None


def estimate_tokens(text: str) -> int:
    return 0 if not text else max(1, round(len(text) / 3.2))


class ReasoningContentAccumulator:
    """Split leading reasoning tags without leaking partial tags into the answer."""

    TAGS = (("<think>", "</think>"), ("<analysis>", "</analysis>"), ("<reasoning>", "</reasoning>"))

    def __init__(self) -> None:
        self.raw = ""
        self.emitted_answer = 0
        self.emitted_thinking = 0

    def append(self, value: str, flush: bool = False) -> tuple[str, str]:
        self.raw += value
        answer, thinking = self._split(self.raw, flush)
        answer_delta = answer[self.emitted_answer :] if len(answer) > self.emitted_answer else ""
        thinking_delta = thinking[self.emitted_thinking :] if len(thinking) > self.emitted_thinking else ""
        self.emitted_answer = len(answer)
        self.emitted_thinking = len(thinking)
        return answer_delta, thinking_delta

    def flush(self) -> tuple[str, str]:
        return self.append("", True)

    @classmethod
    def _split(cls, value: str, flush: bool) -> tuple[str, str]:
        stripped = value.lstrip()
        whitespace = len(value) - len(stripped)
        matched = next(((opening, closing) for opening, closing in cls.TAGS if stripped.lower().startswith(opening)), None)
        if matched is None:
            if not flush and any(opening.startswith(stripped.lower()) for opening, _ in cls.TAGS):
                return "", ""
            return value, ""
        opening, closing = matched
        thinking_start = whitespace + len(opening)
        close_at = value.lower().find(closing, thinking_start)
        if close_at >= 0:
            return value[close_at + len(closing) :].lstrip(), value[thinking_start:close_at].strip()
        thinking = value[thinking_start:]
        if not flush:
            for length in range(min(len(thinking), len(closing) - 1), 0, -1):
                if thinking.lower().endswith(closing[:length]):
                    thinking = thinking[:-length]
                    break
        return "", thinking.lstrip()


class LinuxHostCore:
    def __init__(self) -> None:
        self.lock = threading.RLock()
        self.settings = load_settings()
        self.api_key = ""
        self.gpu = detect_gpu()
        self.engine = EngineInfo(root=normalize_engine_root(self.settings["api_url"]))
        self.models: list[str] = [self.settings["model"]] if self.settings["model"] else []
        self.stop_event = threading.Event()
        self.worker: threading.Thread | None = None
        self.active_model_response: Any = None
        self.host_id: str | None = None
        self.host_token: str | None = None
        self.sharing = False
        self.connected = False
        self.status_en, self.status_ar = "Idle", "خامل"
        self.detail_en, self.detail_ar = "Sharing is off", "المشاركة متوقفة"

    @property
    def language(self) -> str:
        return "ar" if self.settings.get("language") == "ar" else "en"

    def _set_status(self, en: str, ar: str, detail_en: str = "", detail_ar: str = "") -> None:
        with self.lock:
            self.status_en, self.status_ar = en, ar
            self.detail_en, self.detail_ar = detail_en, detail_ar

    def state(self) -> dict[str, Any]:
        with self.lock:
            return {
                "settings": {
                    "deviceId": self.settings["device_id"],
                    "deviceName": self.settings["device_name"],
                    "serverUrl": OFFICIAL_COORDINATOR,
                    "apiUrl": self.settings["api_url"],
                    "mode": "openai",
                    "model": self.settings["model"],
                    "language": self.language,
                    "hasPlatformKey": bool(self.settings.get("platform_key")),
                    "hasApiKey": bool(self.api_key),
                },
                "gpu": {"vendor": self.gpu.vendor, "name": self.gpu.name, "vramGb": self.gpu.vram_gb},
                "sharing": self.sharing,
                "connected": self.connected,
                "status": self.status_ar if self.language == "ar" else self.status_en,
                "detail": self.detail_ar if self.language == "ar" else self.detail_en,
                "activity": self.detail_ar if self.language == "ar" else self.detail_en,
                "engine": {
                    "kind": self.engine.kind,
                    "name": self.engine.name,
                    "version": self.engine.version,
                    "contextLength": self.engine.context_length,
                },
                "models": list(self.models),
            }

    def save(self, payload: dict[str, Any]) -> dict[str, Any]:
        with self.lock:
            mapping = {"deviceName": "device_name", "apiUrl": "api_url", "model": "model", "language": "language", "mode": "mode"}
            for source, target in mapping.items():
                if source in payload:
                    self.settings[target] = str(payload[source]).strip()
            platform_key = str(payload.get("platformKey", "")).strip()
            if platform_key:
                self.settings["platform_key"] = platform_key
            api_key = str(payload.get("apiKey", "")).strip()
            if api_key:
                self.api_key = api_key
            self.settings["server_url"] = OFFICIAL_COORDINATOR
            save_settings(self.settings)
        return self.state()

    def _engine_headers(self) -> dict[str, str]:
        return {"Authorization": f"Bearer {self.api_key}"} if self.api_key else {}

    def refresh_models(self) -> dict[str, Any]:
        root = normalize_engine_root(self.settings["api_url"])
        headers = self._engine_headers()
        probes: dict[str, dict[str, Any] | None] = {}
        paths = {"ollama_version": "api/version", "ollama_tags": "api/tags", "lm_v1": "api/v1/models", "lm_v0": "api/v0/models", "llama": "props"}
        threads = []
        def probe(name: str, path: str) -> None:
            probes[name] = try_json(api_endpoint(root, path), headers)
        for name, path in paths.items():
            thread = threading.Thread(target=probe, args=(name, path))
            threads.append(thread); thread.start()
        for thread in threads:
            thread.join()

        ollama_version = probes.get("ollama_version") or {}
        ollama_tags = probes.get("ollama_tags") or {}
        lm_v1 = probes.get("lm_v1") or {}
        lm_v0 = probes.get("lm_v0") or {}
        llama_props = probes.get("llama") or {}
        if isinstance(ollama_version.get("version"), str) and isinstance(ollama_tags.get("models"), list):
            detected = EngineInfo("Ollama", "Ollama", str(ollama_version["version"]), root)
        elif isinstance(lm_v1.get("models"), list):
            detected = EngineInfo("LmStudioV1", "LM Studio", "v1 API", root)
        elif isinstance(lm_v0.get("data"), list) and lm_v0.get("object") == "list":
            detected = EngineInfo("LmStudioLegacy", "LM Studio", "legacy API", root)
        elif (isinstance(llama_props.get("default_generation_settings"), dict)
              and llama_props.get("build_info") is not None
              and any(key in llama_props for key in ("model_path", "chat_template", "total_slots"))):
            detected = EngineInfo("LlamaCpp", "llama.cpp", str(llama_props.get("build_info", "")), root)
        else:
            detected = EngineInfo(root=root)

        try:
            model_payload = request_json(api_endpoint(self.settings["api_url"], "models"), headers=headers, timeout=10)
        except RuntimeError:
            if detected.kind != "Ollama":
                raise
            model_payload = {"data": []}
        names = [str(item["id"]) for item in model_payload.get("data", []) if isinstance(item, dict) and item.get("id")]
        if not names and detected.kind == "Ollama":
            names = [str(item.get("name") or item.get("model")) for item in ollama_tags.get("models", []) if item.get("name") or item.get("model")]
        if names and self.settings["model"] not in names:
            self.settings["model"] = names[0]

        context_payload: Any = model_payload
        if detected.kind == "Ollama" and self.settings["model"]:
            context_payload = request_json(api_endpoint(root, "api/show"), method="POST", body={"model": self.settings["model"]}, headers=headers, timeout=5)
        elif detected.kind == "LlamaCpp":
            context_payload = llama_props
        elif detected.kind == "LmStudioV1":
            context_payload = lm_v1
        elif detected.kind == "LmStudioLegacy":
            context_payload = lm_v0
        self.engine = EngineInfo(detected.kind, detected.name, detected.version, detected.root, find_context_length(context_payload, self.settings["model"]))
        self.models = names
        save_settings(self.settings)
        self._set_status("Models detected", "تم اكتشاف النماذج", f"Found {len(names)} local model(s)", f"عُثر على {len(names)} نموذج محلي")
        return self.state()

    def start(self) -> dict[str, Any]:
        with self.lock:
            if self.worker and self.worker.is_alive():
                return self.state()
            if not self.settings["device_name"] or not self.settings["api_url"] or not self.settings["model"]:
                raise RuntimeError("Complete the device name, local API URL, and model.")
            if not self.settings["api_url"].startswith(("http://", "https://")):
                raise RuntimeError("Enter a valid HTTP or HTTPS local API URL.")
        try:
            self.refresh_models()
        except Exception:
            self.engine = EngineInfo(root=normalize_engine_root(self.settings["api_url"]))
        self.stop_event.clear()
        self.sharing = True
        self._set_status("Connecting…", "جاري الاتصال…", "Checking the AI Palm coordinator", "نتحقق من خادم نخلة AI")
        self.worker = threading.Thread(target=self._worker_loop, daemon=True, name="ai-palm-sharing")
        self.worker.start()
        return self.state()

    def stop(self) -> dict[str, Any]:
        self.stop_event.set()
        with self.lock:
            worker = self.worker
            active_response = self.active_model_response
        if active_response is not None:
            try:
                active_response.close()
            except Exception:
                pass
        try:
            self._heartbeat(False)
        except Exception:
            pass
        if worker and worker is not threading.current_thread():
            worker.join(timeout=6)
        with self.lock:
            self.sharing = False
            self.connected = False
            self.host_id = self.host_token = None
            if self.worker is worker and (worker is None or not worker.is_alive()):
                self.worker = None
        self._set_status("Idle", "خامل", "Sharing has stopped", "تم إيقاف المشاركة")
        return self.state()

    def _host_headers(self) -> dict[str, str]:
        return {"X-Host-Token": self.host_token or ""}

    def _worker_loop(self) -> None:
        try:
            registration = request_json(
                f"{OFFICIAL_COORDINATOR}/api/hosts/register",
                method="POST",
                headers={"X-Registration-Key": str(self.settings.get("platform_key", ""))},
                body={
                    "host_id": self.settings["device_id"], "name": self.settings["device_name"],
                    "gpu": self.gpu.name, "gpu_vendor": self.gpu.vendor, "vram_gb": self.gpu.vram_gb,
                    "context_length": self.engine.context_length, "mode": "openai", "model": self.settings["model"],
                    "capabilities": ["chat"], "enabled": True,
                },
            )
            self.host_id, self.host_token = str(registration["host_id"]), str(registration["token"])
            self.connected = True
            self._set_status("Connected — sharing is active", "متصل — المشاركة مفعلة", f"Model: {self.settings['model']}", f"النموذج: {self.settings['model']}")
            while not self.stop_event.is_set():
                self._heartbeat(True)
                response = request_json(f"{OFFICIAL_COORDINATOR}/api/hosts/{self.host_id}/jobs/next", headers=self._host_headers(), timeout=4)
                job = response.get("job")
                if job:
                    self._run_job(job)
                else:
                    self.stop_event.wait(1.5)
        except Exception as error:
            if not self.stop_event.is_set():
                self._set_status("Connection failed", "تعذر الاتصال", str(error), str(error))
        finally:
            with self.lock:
                self.connected = False
                if not self.stop_event.is_set():
                    self.sharing = False
                if self.worker is threading.current_thread():
                    self.worker = None

    def _heartbeat(self, enabled: bool) -> None:
        if self.host_id and self.host_token:
            request_json(f"{OFFICIAL_COORDINATOR}/api/hosts/{self.host_id}/heartbeat", method="POST", headers=self._host_headers(), body={"enabled": enabled})

    def _run_job(self, job: dict[str, Any]) -> None:
        job_id = str(job["id"])
        prompt = str(job.get("prompt", ""))
        messages = job.get("messages") or [{"role": "user", "content": prompt}]
        self._set_status("Running a task", "يتم تنفيذ مهمة الآن", f"Task: {job_id}", f"المهمة: {job_id}")
        done = threading.Event()
        threading.Thread(target=self._heartbeat_during, args=(done,), daemon=True).start()
        try:
            self._run_model(messages, job_id)
            request_json(f"{OFFICIAL_COORDINATOR}/api/jobs/{job_id}/complete", method="POST", headers=self._host_headers(), body={"result": ""})
            self._set_status("Connected — ready for a new task", "متصل — جاهز لمهمة جديدة", f"Model: {self.settings['model']}", f"النموذج: {self.settings['model']}")
        except Exception as error:
            try:
                request_json(f"{OFFICIAL_COORDINATOR}/api/jobs/{job_id}/fail", method="POST", headers=self._host_headers(), body={"error": str(error)})
            except Exception:
                pass
            self._set_status("Task failed", "فشلت المهمة", str(error), str(error))
        finally:
            done.set()

    def _heartbeat_during(self, done: threading.Event) -> None:
        while not done.wait(5) and not self.stop_event.is_set():
            try:
                self._heartbeat(True)
            except Exception:
                pass

    @staticmethod
    def _exact_usage(exact: dict[str, Any] | None, stats: dict[str, Any] | None = None, timings: dict[str, Any] | None = None) -> dict[str, Any] | None:
        exact, stats, timings = exact or {}, stats or {}, timings or {}
        prompt_tokens = exact.get("prompt_tokens", stats.get("input_tokens", timings.get("prompt_n")))
        completion_tokens = exact.get("completion_tokens", stats.get("total_output_tokens", timings.get("predicted_n")))
        if not isinstance(prompt_tokens, (int, float)) and not isinstance(completion_tokens, (int, float)):
            return None
        prompt_tokens = int(prompt_tokens or 0)
        completion_tokens = int(completion_tokens or 0)
        total = exact.get("total_tokens")
        return {
            "prompt_tokens": prompt_tokens,
            "completion_tokens": completion_tokens,
            "total_tokens": int(total) if isinstance(total, (int, float)) else prompt_tokens + completion_tokens,
            "tokens_per_second": exact.get("tokens_per_second"),
            "estimated": False,
        }

    @staticmethod
    def _ollama_usage(chunk: dict[str, Any]) -> dict[str, Any] | None:
        prompt_tokens, completion_tokens = chunk.get("prompt_eval_count"), chunk.get("eval_count")
        if not isinstance(prompt_tokens, (int, float)) and not isinstance(completion_tokens, (int, float)):
            return None
        prompt_tokens, completion_tokens = int(prompt_tokens or 0), int(completion_tokens or 0)
        duration = chunk.get("eval_duration")
        speed = round(completion_tokens / (float(duration) / 1_000_000_000), 1) if completion_tokens and isinstance(duration, (int, float)) and duration > 0 else None
        return {"prompt_tokens": prompt_tokens, "completion_tokens": completion_tokens,
                "total_tokens": prompt_tokens + completion_tokens, "tokens_per_second": speed, "estimated": False}

    def _publish(self, job_id: str, delta: str, thinking: str, usage: dict[str, Any] | None) -> None:
        safe_size = 16_000
        answer_parts = [delta[index:index + safe_size] for index in range(0, len(delta), safe_size)] or [""]
        thinking_parts = [thinking[index:index + safe_size] for index in range(0, len(thinking), safe_size)] or [""]
        count = max(len(answer_parts), len(thinking_parts))
        for index in range(count):
            answer_part = answer_parts[index] if index < len(answer_parts) else ""
            thinking_part = thinking_parts[index] if index < len(thinking_parts) else ""
            final_usage = usage if index == count - 1 else None
            body = {"delta": answer_part, "thinking_delta": thinking_part, "usage": final_usage}
            try:
                request_json(f"{OFFICIAL_COORDINATOR}/api/jobs/{job_id}/stream", method="POST", headers=self._host_headers(), body=body, timeout=20)
            except RuntimeError as error:
                if not thinking_part:
                    raise
                encoded = __import__("base64").b64encode(thinking_part.encode("utf-8")).decode("ascii")
                fallback = {"delta": f"[[AI_PALM_REASONING_BASE64:{encoded}]]{answer_part}", "usage": final_usage}
                try:
                    request_json(f"{OFFICIAL_COORDINATOR}/api/jobs/{job_id}/stream", method="POST", headers=self._host_headers(), body=fallback, timeout=20)
                except Exception:
                    raise error

    def _run_model(self, messages: list[dict[str, Any]], job_id: str) -> tuple[str, dict[str, Any] | None]:
        if self.engine.kind == "Ollama":
            url = api_endpoint(self.engine.root, "api/chat")
            body = {"model": self.settings["model"], "stream": True, "messages": messages}
            native_ollama = True
        else:
            url = api_endpoint(self.settings["api_url"], "chat/completions")
            body = {"model": self.settings["model"], "stream": True, "stream_options": {"include_usage": True}, "messages": messages}
            native_ollama = False
        headers = {"Content-Type": "application/json", "Accept": "application/x-ndjson" if native_ollama else "text/event-stream", **self._engine_headers()}
        request = urllib.request.Request(url, data=json.dumps(body).encode("utf-8"), headers=headers, method="POST")
        try:
            response = urllib.request.urlopen(request, timeout=1800)
        except urllib.error.HTTPError as error:
            raise RuntimeError(error.read().decode("utf-8", "replace")) from error

        with self.lock:
            self.active_model_response = response
        try:
            return self._consume_model_response(response, native_ollama, job_id)
        finally:
            with self.lock:
                if self.active_model_response is response:
                    self.active_model_response = None

    def _consume_model_response(self, response: Any, native_ollama: bool, job_id: str) -> tuple[str, dict[str, Any] | None]:
        result, pending, pending_thinking, exact = "", "", "", None
        with response:
            content_type = response.headers.get_content_type()
            if not native_ollama and content_type != "text/event-stream":
                payload = json.loads(response.read().decode("utf-8"))
                message = (payload.get("choices") or [{}])[0].get("message", {})
                accumulator = ReasoningContentAccumulator()
                answer, embedded = accumulator.append(str(message.get("content") or ""), True)
                thinking = str(message.get("reasoning_content") or message.get("reasoning") or message.get("thinking") or "") + embedded
                usage = self._exact_usage(payload.get("usage"), payload.get("stats"), payload.get("timings"))
                self._publish(job_id, answer, thinking, usage)
                return answer, usage

            accumulator = ReasoningContentAccumulator()
            exact_stats = exact_timings = None
            for raw_line in response:
                if self.stop_event.is_set():
                    raise RuntimeError("Sharing has stopped")
                line = raw_line.decode("utf-8", "replace").strip()
                if not line:
                    continue
                data = line if native_ollama else line[5:].strip() if line.startswith("data:") else ""
                if not data or data == "[DONE]":
                    continue
                try:
                    chunk = json.loads(data)
                except ValueError:
                    continue
                if native_ollama:
                    message = chunk.get("message") or {}
                    raw_delta = str(message.get("content") or "")
                    thinking = str(message.get("thinking") or "")
                    if chunk.get("done"):
                        exact = self._ollama_usage(chunk)
                else:
                    choice = (chunk.get("choices") or [{}])[0]
                    delta_object = choice.get("delta") or {}
                    raw_delta = str(delta_object.get("content") or "")
                    thinking = str(delta_object.get("reasoning_content") or delta_object.get("reasoning") or delta_object.get("thinking") or "")
                    exact = chunk.get("usage") or exact
                    exact_stats = chunk.get("stats") or exact_stats
                    exact_timings = chunk.get("timings") or exact_timings
                delta, embedded_thinking = accumulator.append(raw_delta)
                thinking += embedded_thinking
                result += delta; pending += delta; pending_thinking += thinking
                if len(pending) >= 64 or len(pending_thinking) >= 64:
                    self._publish(job_id, pending, pending_thinking, None)
                    pending = pending_thinking = ""
        tail, embedded = accumulator.flush()
        result += tail
        pending += tail
        pending_thinking += embedded
        if pending or pending_thinking:
            self._publish(job_id, pending, pending_thinking, None)
        usage = exact if native_ollama else self._exact_usage(exact, exact_stats, exact_timings)
        self._publish(job_id, "", "", usage)
        return result or "The model returned no answer", usage

    def coordinator_get(self, path: str) -> dict[str, Any]:
        self._validate_coordinator_path(path)
        return request_json(f"{OFFICIAL_COORDINATOR}{path}")

    def coordinator_post(self, path: str, body: Any) -> dict[str, Any]:
        self._validate_coordinator_path(path)
        return request_json(f"{OFFICIAL_COORDINATOR}{path}", method="POST", body=body)

    @staticmethod
    def _validate_coordinator_path(path: str) -> None:
        if not path.startswith("/api/") or "://" in path:
            raise RuntimeError("Only AI Palm API paths are allowed.")


class DesktopBridge:
    def __init__(self, core: LinuxHostCore) -> None:
        self.core = core
        self.window: Any = None

    def rpc(self, message: dict[str, Any]) -> dict[str, Any]:
        message_id = str(message.get("id", ""))
        action = str(message.get("action", ""))
        payload = message.get("payload") or {}
        try:
            if action == "state":
                data = self.core.state()
            elif action == "saveSettings":
                data = self.core.save(payload)
            elif action == "refreshModels":
                data = self.core.refresh_models()
            elif action == "startSharing":
                data = self.core.start()
            elif action == "stopSharing":
                data = self.core.stop()
            elif action == "apiGet":
                data = self.core.coordinator_get(str(payload.get("path", "")))
            elif action == "apiPost":
                data = self.core.coordinator_post(str(payload.get("path", "")), payload.get("body") or {})
            elif action == "minimize":
                if self.window:
                    self.window.minimize()
                data = {"ok": True}
            else:
                raise RuntimeError(f"Unknown desktop action: {action}")
            return {"id": message_id, "ok": True, "data": data, "error": None}
        except Exception as error:
            return {"id": message_id, "ok": False, "data": None, "error": str(error)}


class DesktopTray:
    def __init__(self, core: LinuxHostCore, window: Any) -> None:
        self.core = core
        self.window = window
        self.icon: Any = None
        self.allow_close = False

    def start(self) -> None:
        if self.icon is not None:
            return
        try:
            from PIL import Image
            import pystray
            image = Image.open(Path(__file__).with_name("ai-palm-mark.png")).convert("RGBA").resize((64, 64), Image.Resampling.LANCZOS)
            self.icon = pystray.Icon("ai-palm", image, "AI Palm", self._menu())
            threading.Thread(target=self.icon.run, daemon=True, name="ai-palm-tray").start()
        except Exception:
            self.icon = None

    def _menu(self):
        import pystray
        return pystray.Menu(
            pystray.MenuItem("Open AI Palm", self.open_window, default=True),
            pystray.MenuItem("Start sharing", self.start_sharing),
            pystray.MenuItem("Stop sharing", self.stop_sharing),
            pystray.MenuItem("Exit", self.exit_app),
        )

    def open_window(self, *_: Any) -> None:
        self.window.show()
        self.window.restore()

    def start_sharing(self, *_: Any) -> None:
        try:
            self.core.start()
        except Exception:
            pass

    def stop_sharing(self, *_: Any) -> None:
        self.core.stop()

    def hide_on_close(self, *_: Any) -> bool:
        if self.allow_close:
            return True
        self.window.hide()
        return False

    def exit_app(self, *_: Any) -> None:
        self.allow_close = True
        self.core.stop()
        if self.icon is not None:
            self.icon.stop()
        self.window.destroy()


def resolve_ui_dir() -> Path:
    packaged = Path(__file__).with_name("ui")
    if (packaged / "index.html").is_file() and (packaged / "ai-palm-mark.png").is_file():
        return packaged

    shared = Path(__file__).resolve().parent.parent / "host-win-webview" / "ui"
    icon = Path(__file__).with_name("ai-palm-mark.png")
    if (shared / "index.html").is_file() and icon.is_file():
        runtime = CONFIG_DIR / "ui" / APP_VERSION
        runtime.mkdir(parents=True, exist_ok=True)
        for name in ("index.html", "styles.css", "layout-fixes.css", "app.js"):
            shutil.copy2(shared / name, runtime / name)
        shutil.copy2(icon, runtime / "ai-palm-mark.png")
        return runtime
    raise RuntimeError("The shared AI Palm UI files are missing.")


def self_test() -> int:
    os.environ["AI_PALM_SETTINGS_PATH"] = str(Path.cwd() / ".ai-palm-self-test-settings.json")
    core = LinuxHostCore()
    bridge = DesktopBridge(core)
    state = bridge.rpc({"id": "1", "action": "state", "payload": {}})
    assert state["ok"] and state["data"]["settings"]["language"] == "en"
    assert bridge.rpc({"id": "2", "action": "apiGet", "payload": {"path": "https://invalid"}})["ok"] is False
    assert normalize_engine_root("http://127.0.0.1:11434/v1") == "http://127.0.0.1:11434"
    assert find_context_length({"models": [{"id": "demo", "context_length": 32768}]}, "demo") == 32768
    ui_dir = resolve_ui_dir()
    assert (ui_dir / "ai-palm-mark.png").is_file()
    print(json.dumps({"ok": True, "version": APP_VERSION, "sharedUi": str(ui_dir)}))
    return 0


def run_desktop() -> int:
    try:
        import webview
    except ImportError as error:
        raise RuntimeError("pywebview is not installed. Run the included install.sh first.") from error
    core = LinuxHostCore()
    bridge = DesktopBridge(core)
    ui_dir = resolve_ui_dir()
    window = webview.create_window(
        "AI Palm", str(ui_dir / "index.html"), js_api=bridge, width=1440, height=900,
        min_size=(1040, 700), background_color="#07110d", text_select=True,
    )
    bridge.window = window
    tray = DesktopTray(core, window)
    window.events.shown += tray.start
    window.events.closing += tray.hide_on_close
    try:
        webview.start(gui="gtk", http_server=True, private_mode=False)
    finally:
        core.stop()
        if tray.icon is not None:
            tray.icon.stop()
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="AI Palm desktop client for Linux")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    return self_test() if args.self_test else run_desktop()


if __name__ == "__main__":
    raise SystemExit(main())

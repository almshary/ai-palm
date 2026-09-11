#!/usr/bin/env python3
"""AI Palm Host for Linux — volunteer an OpenAI-compatible local model."""

from __future__ import annotations

import argparse
import json
import os
import queue
import socket
import subprocess
import sys
import threading
import time
import uuid
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Any


APP_VERSION = "0.7.0"
CONFIG_DIR = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "nawah-host"
CONFIG_PATH = Path(os.environ.get("NAWAH_SETTINGS_PATH", CONFIG_DIR / "settings.json"))

TEXT = {
    "en": {
        "title": "AI Palm — Share your local model", "heading": "AI Palm Host",
        "subheading": "Share your model when you choose — AI Palm stays in the system tray",
        "language": "Language", "device": "Device name", "server": "AI Palm API URL",
        "platform_key": "Platform connection key", "api": "OpenAI-compatible API base URL",
        "api_key": "API key — optional and never saved", "model": "Available model",
        "refresh": "Refresh models", "connect": "Connect and start sharing", "stop": "Stop",
        "gpu": "Detected GPU", "vendor": "Vendor", "name": "Name", "vram": "VRAM",
        "activity": "Latest activity", "offline": "Offline", "setup": "Review the settings, then select Connect",
        "none": "No tasks yet", "searching": "Searching...", "found": "Found {count} local model(s)",
        "model_error": "Could not load models. Check the API URL and key.",
        "missing": "Complete the platform URL, device name, model, and platform key.",
        "invalid": "Enter valid HTTP or HTTPS URLs.", "connecting": "Connecting...",
        "checking": "Checking the platform server", "connected": "Connected — sharing is active",
        "model_value": "Model: {model}", "connected_id": "Connected. Device ID: {id}",
        "connection_failed": "Connection failed", "check_server": "Check the server URL and platform key.",
        "running": "Running a task", "task": "Task: {id}", "started": "Started task {id}",
        "ready": "Connected — ready for a new task", "completed": "Completed task {id}",
        "failed": "Task failed: {error}", "lost": "Connection lost", "reconnect": "Stop sharing, then reconnect.",
        "stopping": "Stopping...", "no_new": "No new tasks will be assigned to this device",
        "stopped": "Sharing has stopped", "tray_open": "Open AI Palm", "tray_start": "Start sharing",
        "tray_stop": "Stop sharing", "tray_exit": "Exit", "unavailable": "Unavailable",
        "no_answer": "The model returned no answer", "timeout": "The connection timed out",
    },
    "ar": {
        "title": "نخلة AI — مشاركة نموذجك المحلي", "heading": "نخلة AI للمضيف",
        "subheading": "شارك نموذجك عندما تريد — وسيبقى بجوار الساعة",
        "language": "اللغة", "device": "اسم الجهاز", "server": "رابط منصة نخلة AI",
        "platform_key": "مفتاح ربط المنصة", "api": "عنوان OpenAI-compatible API الأساسي",
        "api_key": "API Key — اختياري ولا يُحفظ", "model": "النموذج المتاح",
        "refresh": "تحديث النماذج", "connect": "اتصال وبدء المشاركة", "stop": "إيقاف",
        "gpu": "البطاقة المكتشفة", "vendor": "النوع", "name": "الاسم", "vram": "VRAM",
        "activity": "آخر نشاط", "offline": "غير متصل", "setup": "اضبط الإعدادات ثم اضغط اتصال",
        "none": "لا توجد مهام بعد", "searching": "جاري البحث...", "found": "عُثر على {count} نموذج محلي",
        "model_error": "تعذر قراءة النماذج. تحقق من عنوان API والمفتاح.",
        "missing": "أكمل رابط المنصة واسم الجهاز والنموذج ومفتاح الربط.",
        "invalid": "أدخل روابط HTTP أو HTTPS صحيحة.", "connecting": "جاري الاتصال...",
        "checking": "نتحقق من خادم المنصة", "connected": "متصل — المشاركة مفعلة",
        "model_value": "النموذج: {model}", "connected_id": "تم الاتصال. معرّف الجهاز: {id}",
        "connection_failed": "تعذر الاتصال", "check_server": "تحقق من رابط الخادم ومفتاح ربط المنصة.",
        "running": "يتم تنفيذ مهمة الآن", "task": "المهمة: {id}", "started": "بدأ تنفيذ المهمة {id}",
        "ready": "متصل — جاهز لمهمة جديدة", "completed": "اكتملت المهمة {id}",
        "failed": "فشلت المهمة: {error}", "lost": "انقطع الاتصال", "reconnect": "أوقف المشاركة ثم أعد الاتصال.",
        "stopping": "جاري الإيقاف...", "no_new": "لن تُسند مهام جديدة إلى هذا الجهاز",
        "stopped": "تم إيقاف المشاركة", "tray_open": "فتح نخلة AI", "tray_start": "بدء المشاركة",
        "tray_stop": "إيقاف المشاركة", "tray_exit": "خروج", "unavailable": "غير متاح",
        "no_answer": "لم يُرجع النموذج إجابة", "timeout": "انتهت مهلة الاتصال",
    },
}


@dataclass
class GpuInfo:
    vendor: str = "Unknown"
    name: str = "No GPU detected"
    vram_gb: int | None = None


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
        quoted = [part for part in line.split('"') if part.strip() and part.strip() != " "]
        name = quoted[-2].strip() if len(quoted) >= 2 else f"{vendor} GPU"
        memory_file = card / "mem_info_vram_total"
        try:
            vram = round(int(memory_file.read_text().strip()) / (1024**3))
        except (OSError, ValueError):
            vram = None
        return GpuInfo(vendor, name, vram)

    for line in pci.splitlines():
        if "VGA" not in line and "3D controller" not in line:
            continue
        lower = line.lower()
        vendor = "AMD" if "amd" in lower or "ati" in lower else "NVIDIA" if "nvidia" in lower else "Intel" if "intel" in lower else "Unknown"
        parts = [part for part in line.split('"') if part.strip()]
        return GpuInfo(vendor, parts[-2].strip() if len(parts) >= 2 else line, None)
    return GpuInfo()


def load_settings() -> dict[str, Any]:
    defaults = {
        "device_id": uuid.uuid4().hex, "device_name": socket.gethostname(),
        "server_url": "https://nawah.almshary.site", "platform_key": "",
        "api_url": "http://127.0.0.1:1234/v1", "model": "", "language": "en",
    }
    try:
        saved = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
        defaults.update({key: value for key, value in saved.items() if key in defaults})
    except (OSError, ValueError, TypeError):
        pass
    return defaults


def save_settings(settings: dict[str, Any]) -> None:
    CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
    CONFIG_PATH.write_text(json.dumps(settings, indent=2, ensure_ascii=False), encoding="utf-8")
    CONFIG_PATH.chmod(0o600)


def api_endpoint(base: str, endpoint: str) -> str:
    return f"{base.strip().rstrip('/')}/{endpoint.lstrip('/')}"


def request_json(url: str, *, method: str = "GET", body: Any = None, headers: dict[str, str] | None = None, timeout: int = 20) -> dict[str, Any]:
    payload = None if body is None else json.dumps(body).encode("utf-8")
    request_headers = {"Accept": "application/json", **(headers or {})}
    if payload is not None:
        request_headers["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data=payload, headers=request_headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        try:
            detail = json.loads(error.read().decode("utf-8")).get("error")
        except Exception:
            detail = None
        raise RuntimeError(detail or f"HTTP {error.code}") from error
    except (urllib.error.URLError, TimeoutError) as error:
        raise RuntimeError(str(getattr(error, "reason", error))) from error


def estimate_tokens(text: str) -> int:
    return 0 if not text else max(1, round(len(text) / 3.2))


class NawahApp:
    def __init__(self) -> None:
        import tkinter as tk
        from tkinter import messagebox, ttk

        self.tk, self.ttk, self.messagebox = tk, ttk, messagebox
        self.root = tk.Tk()
        self.settings = load_settings()
        self.lang = "ar" if self.settings.get("language") == "ar" else "en"
        self.gpu = detect_gpu()
        self.stop_event = threading.Event()
        self.worker: threading.Thread | None = None
        self.host_id: str | None = None
        self.host_token: str | None = None
        self.tray = None
        self.tray_thread: threading.Thread | None = None
        self.connected = False
        self.localized: list[tuple[Any, str]] = []

        self.device_var = tk.StringVar(value=self.settings["device_name"])
        self.server_var = tk.StringVar(value=self.settings["server_url"])
        self.platform_key_var = tk.StringVar(value=self.settings["platform_key"])
        self.api_var = tk.StringVar(value=self.settings["api_url"])
        self.api_key_var = tk.StringVar()
        self.model_var = tk.StringVar(value=self.settings["model"])
        self.language_var = tk.StringVar(value="العربية" if self.lang == "ar" else "English")
        self.status_var = tk.StringVar()
        self.detail_var = tk.StringVar()
        self.activity_var = tk.StringVar()
        self.status_keys = ("offline", "setup", {})
        self.activity_key = ("none", {})

        self._build_ui()
        self.apply_language()
        self.root.protocol("WM_DELETE_WINDOW", self.hide_to_tray)
        self.root.bind("<Unmap>", self._on_unmap)

    def t(self, key: str, **values: Any) -> str:
        return TEXT[self.lang][key].format(**values)

    def _style(self) -> None:
        style = self.ttk.Style(self.root)
        try:
            style.theme_use("clam")
        except self.tk.TclError:
            pass
        style.configure("Nawah.TFrame", background="#0b0e0d")
        style.configure("Card.TFrame", background="#171c1a")
        style.configure("Nawah.TLabel", background="#0b0e0d", foreground="#f2f5ef", font=("Segoe UI", 10))
        style.configure("Card.TLabel", background="#171c1a", foreground="#f2f5ef", font=("Segoe UI", 10))
        style.configure("Muted.TLabel", background="#171c1a", foreground="#99a29b", font=("Segoe UI", 9))
        style.configure("Heading.TLabel", background="#0b0e0d", foreground="#f2f5ef", font=("Segoe UI", 20, "bold"))
        style.configure("Accent.TButton", background="#e5c276", foreground="#092016", padding=(16, 11), font=("Segoe UI", 10, "bold"))
        style.map("Accent.TButton", background=[("active", "#c8ff82"), ("disabled", "#46503e")])
        style.configure("Nawah.TButton", background="#202622", foreground="#f2f5ef", padding=(14, 10), font=("Segoe UI", 9, "bold"))
        style.configure("Nawah.TEntry", fieldbackground="#202622", foreground="#f2f5ef", insertcolor="#e5c276", padding=9)
        style.configure("Nawah.TCombobox", fieldbackground="#202622", background="#202622", foreground="#f2f5ef", padding=7)

    def _build_ui(self) -> None:
        self._style()
        self.root.geometry("760x790")
        self.root.minsize(680, 650)
        self.root.configure(bg="#0b0e0d")
        outer = self.ttk.Frame(self.root, style="Nawah.TFrame", padding=30)
        outer.pack(fill="both", expand=True)

        self.heading = self.ttk.Label(outer, style="Heading.TLabel")
        self.heading.pack(anchor="w")
        self.subheading = self.ttk.Label(outer, style="Nawah.TLabel")
        self.subheading.pack(anchor="w", pady=(4, 18))
        self.localized += [(self.heading, "heading"), (self.subheading, "subheading")]

        status = self.ttk.Frame(outer, style="Card.TFrame", padding=18)
        status.pack(fill="x", pady=(0, 14))
        self.status_label = self.ttk.Label(status, textvariable=self.status_var, style="Card.TLabel", font=("Segoe UI", 12, "bold"))
        self.status_label.pack(anchor="w")
        self.ttk.Label(status, textvariable=self.detail_var, style="Muted.TLabel").pack(anchor="w", pady=(5, 0))

        form = self.ttk.Frame(outer, style="Card.TFrame", padding=20)
        form.pack(fill="both", expand=True)
        form.columnconfigure(0, weight=1)
        row = 0
        self.language = self._field(form, row, "language", self.language_var, combo=["English", "العربية"]); row += 2
        self.device = self._field(form, row, "device", self.device_var); row += 2
        self.server = self._field(form, row, "server", self.server_var); row += 2
        self.platform_key = self._field(form, row, "platform_key", self.platform_key_var, secret=True); row += 2
        self.api = self._field(form, row, "api", self.api_var); row += 2
        self.api_key = self._field(form, row, "api_key", self.api_key_var, secret=True); row += 2

        model_label = self.ttk.Label(form, style="Muted.TLabel")
        model_label.grid(row=row, column=0, sticky="ew", pady=(8, 4)); row += 1
        self.localized.append((model_label, "model"))
        model_row = self.ttk.Frame(form, style="Card.TFrame")
        model_row.grid(row=row, column=0, sticky="ew"); model_row.columnconfigure(0, weight=1); row += 1
        self.model = self.ttk.Combobox(model_row, textvariable=self.model_var, style="Nawah.TCombobox")
        self.model.grid(row=0, column=0, sticky="ew", padx=(0, 8))
        self.refresh_button = self.ttk.Button(model_row, style="Nawah.TButton", command=self.refresh_models)
        self.refresh_button.grid(row=0, column=1)
        self.localized.append((self.refresh_button, "refresh"))

        gpu = self.ttk.Frame(form, style="Card.TFrame", padding=(0, 16, 0, 4))
        gpu.grid(row=row, column=0, sticky="ew"); row += 1
        self.gpu_caption = self.ttk.Label(gpu, style="Muted.TLabel")
        self.gpu_caption.pack(anchor="w")
        self.gpu_text = self.ttk.Label(gpu, style="Card.TLabel", font=("Segoe UI", 10, "bold"))
        self.gpu_text.pack(anchor="w", pady=(5, 0))
        self.localized.append((self.gpu_caption, "gpu"))

        actions = self.ttk.Frame(outer, style="Nawah.TFrame")
        actions.pack(fill="x", pady=(14, 8)); actions.columnconfigure(0, weight=1)
        self.connect_button = self.ttk.Button(actions, style="Accent.TButton", command=self.start_sharing)
        self.connect_button.grid(row=0, column=0, sticky="ew", padx=(0, 8))
        self.stop_button = self.ttk.Button(actions, style="Nawah.TButton", command=self.stop_sharing, state="disabled")
        self.stop_button.grid(row=0, column=1)
        self.localized += [(self.connect_button, "connect"), (self.stop_button, "stop")]
        self.activity_caption = self.ttk.Label(outer, style="Nawah.TLabel")
        self.activity_caption.pack(anchor="w", pady=(5, 2))
        self.ttk.Label(outer, textvariable=self.activity_var, style="Nawah.TLabel", wraplength=690).pack(anchor="w")
        self.localized.append((self.activity_caption, "activity"))
        self.language.bind("<<ComboboxSelected>>", self.change_language)

    def _field(self, parent: Any, row: int, key: str, variable: Any, *, combo: list[str] | None = None, secret: bool = False) -> Any:
        label = self.ttk.Label(parent, style="Muted.TLabel")
        label.grid(row=row, column=0, sticky="ew", pady=(7, 3))
        self.localized.append((label, key))
        if combo is not None:
            widget = self.ttk.Combobox(parent, textvariable=variable, values=combo, state="readonly", style="Nawah.TCombobox")
        else:
            widget = self.ttk.Entry(parent, textvariable=variable, show="•" if secret else "", style="Nawah.TEntry")
        widget.grid(row=row + 1, column=0, sticky="ew")
        return widget

    def apply_language(self) -> None:
        self.root.title(self.t("title"))
        for widget, key in self.localized:
            widget.configure(text=self.t(key))
        self.status_var.set(self.t(self.status_keys[0], **self.status_keys[2]))
        self.detail_var.set(self.t(self.status_keys[1], **self.status_keys[2]))
        self.activity_var.set(self.t(self.activity_key[0], **self.activity_key[1]))
        vram = f"{self.gpu.vram_gb} GB" if self.gpu.vram_gb is not None else self.t("unavailable")
        self.gpu_text.configure(text=f"{self.t('vendor')}: {self.gpu.vendor}   ·   {self.t('name')}: {self.gpu.name}   ·   {self.t('vram')}: {vram}")
        self._update_tray_menu()

    def change_language(self, _event: Any = None) -> None:
        self.lang = "ar" if self.language_var.get() == "العربية" else "en"
        self.settings["language"] = self.lang
        self._save_current()
        self.apply_language()

    def set_status(self, title: str, detail: str, **values: Any) -> None:
        self.status_keys = (title, detail, values)
        self.status_var.set(self.t(title, **values))
        self.detail_var.set(self.t(detail, **values))

    def set_activity(self, key: str, **values: Any) -> None:
        self.activity_key = (key, values)
        self.activity_var.set(self.t(key, **values))

    def ui(self, callback: Any) -> None:
        try:
            self.root.after(0, callback)
        except self.tk.TclError:
            pass

    def _save_current(self) -> None:
        self.settings.update({
            "device_name": self.device_var.get().strip(), "server_url": self.server_var.get().strip().rstrip("/"),
            "platform_key": self.platform_key_var.get(), "api_url": self.api_var.get().strip().rstrip("/"),
            "model": self.model_var.get().strip(), "language": self.lang,
        })
        save_settings(self.settings)

    def refresh_models(self) -> None:
        self.refresh_button.configure(state="disabled", text=self.t("searching"))
        threading.Thread(target=self._refresh_models_worker, daemon=True).start()

    def _refresh_models_worker(self) -> None:
        try:
            headers = {"Authorization": f"Bearer {self.api_key_var.get()}"} if self.api_key_var.get().strip() else {}
            payload = request_json(api_endpoint(self.api_var.get(), "models"), headers=headers)
            names = [item.get("id") for item in payload.get("data", []) if item.get("id")]
            def success() -> None:
                self.model.configure(values=names)
                if names and self.model_var.get() not in names:
                    self.model_var.set(names[0])
                self.set_activity("found", count=len(names))
            self.ui(success)
        except Exception:
            self.ui(lambda: self.set_activity("model_error"))
        finally:
            self.ui(lambda: self.refresh_button.configure(state="normal", text=self.t("refresh")))

    @staticmethod
    def _valid_url(value: str) -> bool:
        return value.startswith("http://") or value.startswith("https://")

    def start_sharing(self) -> None:
        if self.worker and self.worker.is_alive():
            return
        if not all([self.device_var.get().strip(), self.server_var.get().strip(), self.platform_key_var.get().strip(), self.api_var.get().strip(), self.model_var.get().strip()]):
            self.messagebox.showwarning(self.t("heading"), self.t("missing")); return
        if not self._valid_url(self.server_var.get()) or not self._valid_url(self.api_var.get()):
            self.messagebox.showwarning(self.t("heading"), self.t("invalid")); return
        self._save_current()
        self.stop_event.clear()
        self._set_connected_controls(True)
        self.set_status("connecting", "checking")
        self.worker = threading.Thread(target=self._worker_loop, daemon=True)
        self.worker.start()

    def _headers(self) -> dict[str, str]:
        return {"X-Host-Token": self.host_token or ""}

    def _worker_loop(self) -> None:
        try:
            registration = request_json(
                f"{self.settings['server_url']}/api/hosts/register", method="POST",
                headers={"X-Registration-Key": self.settings["platform_key"]},
                body={"host_id": self.settings["device_id"], "name": self.settings["device_name"],
                      "gpu": self.gpu.name, "gpu_vendor": self.gpu.vendor, "vram_gb": self.gpu.vram_gb,
                      "mode": "openai", "model": self.settings["model"], "capabilities": ["chat"], "enabled": True},
            )
            self.host_id, self.host_token = registration["host_id"], registration["token"]
            self.connected = True
            self.ui(lambda: (self.set_status("connected", "model_value", model=self.settings["model"]), self.set_activity("connected_id", id=self.host_id)))
            while not self.stop_event.is_set():
                self._heartbeat(True)
                response = request_json(f"{self.settings['server_url']}/api/hosts/{self.host_id}/jobs/next", headers=self._headers())
                job = response.get("job")
                if not job:
                    self.stop_event.wait(1.5)
                    continue
                self._run_job(job)
        except Exception as error:
            if not self.stop_event.is_set():
                self.ui(lambda error=error: (self.set_status("connection_failed", "check_server"), self.set_activity("failed", error=str(error)), self._set_connected_controls(False)))
        finally:
            self.connected = False

    def _heartbeat(self, enabled: bool) -> None:
        if self.host_id and self.host_token:
            request_json(f"{self.settings['server_url']}/api/hosts/{self.host_id}/heartbeat", method="POST", headers=self._headers(), body={"enabled": enabled})

    def _heartbeat_during(self, done: threading.Event) -> None:
        while not done.wait(5) and not self.stop_event.is_set():
            try:
                self._heartbeat(True)
            except Exception:
                pass

    def _run_job(self, job: dict[str, Any]) -> None:
        job_id, prompt = str(job["id"]), str(job.get("prompt", ""))
        messages = job.get("messages") or [{"role": "user", "content": prompt}]
        self.ui(lambda: (self.set_status("running", "task", id=job_id), self.set_activity("started", id=job_id)))
        heartbeat_done = threading.Event()
        threading.Thread(target=self._heartbeat_during, args=(heartbeat_done,), daemon=True).start()
        try:
            result, usage = self._run_model(prompt, messages, job_id)
            request_json(f"{self.settings['server_url']}/api/jobs/{job_id}/complete", method="POST", headers=self._headers(), body={"result": result, "usage": usage})
            self.ui(lambda: (self.set_status("ready", "model_value", model=self.settings["model"]), self.set_activity("completed", id=job_id)))
        except Exception as error:
            try:
                request_json(f"{self.settings['server_url']}/api/jobs/{job_id}/fail", method="POST", headers=self._headers(), body={"error": str(error)})
            except Exception:
                pass
            self.ui(lambda error=error: self.set_activity("failed", error=str(error)))
        finally:
            heartbeat_done.set()

    def _usage(self, prompt: str, result: str, started: float, exact: dict[str, Any] | None = None) -> dict[str, Any]:
        exact = exact or {}
        prompt_tokens = exact.get("prompt_tokens", estimate_tokens(prompt))
        completion_tokens = exact.get("completion_tokens", estimate_tokens(result))
        elapsed = max(time.monotonic() - started, 0.001)
        return {"prompt_tokens": prompt_tokens, "completion_tokens": completion_tokens,
                "total_tokens": exact.get("total_tokens", prompt_tokens + completion_tokens),
                "tokens_per_second": round(completion_tokens / elapsed, 1), "estimated": not bool(exact)}

    def _publish(self, job_id: str, delta: str, usage: dict[str, Any]) -> None:
        request_json(f"{self.settings['server_url']}/api/jobs/{job_id}/stream", method="POST", headers=self._headers(), body={"delta": delta, "usage": usage}, timeout=20)

    def _run_model(self, prompt: str, messages: list[dict[str, str]], job_id: str) -> tuple[str, dict[str, Any]]:
        usage_prompt = "\n".join(str(message.get("content", "")) for message in messages)
        body = json.dumps({"model": self.settings["model"], "stream": True, "stream_options": {"include_usage": True}, "messages": messages}).encode("utf-8")
        headers = {"Content-Type": "application/json", "Accept": "text/event-stream"}
        if self.api_key_var.get().strip():
            headers["Authorization"] = f"Bearer {self.api_key_var.get().strip()}"
        request = urllib.request.Request(api_endpoint(self.settings["api_url"], "chat/completions"), data=body, headers=headers, method="POST")
        started = time.monotonic()
        try:
            response = urllib.request.urlopen(request, timeout=1800)
        except urllib.error.HTTPError as error:
            raise RuntimeError(error.read().decode("utf-8", "replace")) from error
        content_type = response.headers.get_content_type()
        if content_type != "text/event-stream":
            payload = json.loads(response.read().decode("utf-8"))
            result = payload.get("choices", [{}])[0].get("message", {}).get("content", "") or self.t("no_answer")
            usage = self._usage(usage_prompt, result, started, payload.get("usage"))
            self._publish(job_id, result, usage)
            return result, usage

        result, pending, exact = "", "", None
        with response:
            for raw in response:
                if self.stop_event.is_set():
                    raise RuntimeError(self.t("stopped"))
                line = raw.decode("utf-8", "replace").strip()
                if not line.startswith("data:"):
                    continue
                data = line[5:].strip()
                if not data or data == "[DONE]":
                    continue
                try:
                    chunk = json.loads(data)
                except ValueError:
                    continue
                delta = chunk.get("choices", [{}])[0].get("delta", {}).get("content") or ""
                result += delta; pending += delta
                exact = chunk.get("usage") or exact
                if len(pending) >= 18:
                    self._publish(job_id, pending, self._usage(usage_prompt, result, started))
                    pending = ""
        if pending:
            self._publish(job_id, pending, self._usage(usage_prompt, result, started))
        usage = self._usage(usage_prompt, result, started, exact)
        self._publish(job_id, "", usage)
        return result or self.t("no_answer"), usage

    def stop_sharing(self) -> None:
        self.set_status("stopping", "no_new")
        self.stop_event.set()
        threading.Thread(target=self._stop_worker, daemon=True).start()

    def _stop_worker(self) -> None:
        try:
            self._heartbeat(False)
        except Exception:
            pass
        self.host_id = self.host_token = None
        self.connected = False
        self.ui(lambda: (self._set_connected_controls(False), self.set_status("offline", "stopped"), self.set_activity("stopped")))

    def _set_connected_controls(self, connected: bool) -> None:
        self.connect_button.configure(state="disabled" if connected else "normal")
        self.stop_button.configure(state="normal" if connected else "disabled")
        state = "disabled" if connected else "normal"
        for widget in (self.device, self.server, self.platform_key, self.api, self.api_key, self.model, self.refresh_button):
            widget.configure(state=state)
        self.language.configure(state="readonly")
        self._update_tray_menu()

    def _tray_image(self) -> Any:
        from PIL import Image
        logo_path = Path(__file__).with_name("ai-palm-mark.png")
        return Image.open(logo_path).convert("RGBA").resize((64, 64), Image.Resampling.LANCZOS)

    def _start_tray(self) -> bool:
        if self.tray is not None:
            return True
        try:
            import pystray
            self.tray = pystray.Icon("ai-palm-host", self._tray_image(), "AI Palm Host")
            self._update_tray_menu()
            self.tray_thread = threading.Thread(target=self.tray.run, daemon=True)
            self.tray_thread.start()
            return True
        except Exception:
            self.tray = None
            return False

    def _update_tray_menu(self) -> None:
        if self.tray is None:
            return
        try:
            import pystray
            self.tray.menu = pystray.Menu(
                pystray.MenuItem(self.t("tray_open"), lambda: self.ui(self.restore)),
                pystray.MenuItem(self.t("tray_start"), lambda: self.ui(self.start_sharing), enabled=not self.connected),
                pystray.MenuItem(self.t("tray_stop"), lambda: self.ui(self.stop_sharing), enabled=self.connected),
                pystray.MenuItem(self.t("tray_exit"), lambda: self.ui(self.exit_app)),
            )
            self.tray.update_menu()
        except Exception:
            pass

    def hide_to_tray(self) -> None:
        if self._start_tray():
            self.root.withdraw()
        else:
            self.root.iconify()

    def _on_unmap(self, _event: Any) -> None:
        self.root.after(120, lambda: self.hide_to_tray() if self.root.state() == "iconic" else None)

    def restore(self) -> None:
        self.root.deiconify(); self.root.state("normal"); self.root.lift(); self.root.focus_force()

    def exit_app(self) -> None:
        self.stop_event.set()
        if self.connected:
            try:
                self._heartbeat(False)
            except Exception:
                pass
        if self.tray is not None:
            try:
                self.tray.stop()
            except Exception:
                pass
        self.root.destroy()

    def run(self) -> None:
        self.root.mainloop()


def main() -> None:
    parser = argparse.ArgumentParser(description="AI Palm Host for Linux")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        settings, gpu = load_settings(), detect_gpu()
        print(json.dumps({"ok": True, "version": APP_VERSION, "language": settings["language"], "gpu_vendor": gpu.vendor, "gpu_name": gpu.name, "vram_gb": gpu.vram_gb}))
        return
    try:
        NawahApp().run()
    except ModuleNotFoundError as error:
        if error.name == "tkinter":
            print("Tkinter is required. On Ubuntu/Debian run: sudo apt install python3-tk", file=sys.stderr)
            raise SystemExit(2) from error
        raise


if __name__ == "__main__":
    main()

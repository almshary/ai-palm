from __future__ import annotations

import importlib.util
import json
import os
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("aipalm_linux.py")
os.environ["AI_PALM_SETTINGS_PATH"] = str(Path(tempfile.gettempdir()) / "ai-palm-linux-test-settings.json")
spec = importlib.util.spec_from_file_location("aipalm_linux", MODULE_PATH)
app = importlib.util.module_from_spec(spec)
assert spec and spec.loader
sys.modules[spec.name] = app
spec.loader.exec_module(app)


class EngineHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        payload = {
            "/api/version": {"version": "0.12.0"},
            "/api/tags": {"models": [{"name": "qwen:test"}]},
            "/v1/models": {"data": [{"id": "qwen:test"}]},
        }.get(self.path)
        if payload is None:
            self.send_response(404); self.end_headers(); return
        raw = json.dumps(payload).encode()
        self.send_response(200); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(raw))); self.end_headers(); self.wfile.write(raw)

    def do_POST(self):
        if self.path != "/api/show":
            self.send_response(404); self.end_headers(); return
        length = int(self.headers.get("Content-Length", "0"))
        self.rfile.read(length)
        raw = json.dumps({"model": "qwen:test", "model_info": {"qwen.context_length": 32768}}).encode()
        self.send_response(200); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(raw))); self.end_headers(); self.wfile.write(raw)

    def log_message(self, *_):
        pass


class LinuxCoreTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), EngineHandler)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown(); cls.server.server_close()

    def test_engine_fingerprint_models_and_context(self):
        core = app.LinuxHostCore()
        core.settings["api_url"] = f"http://127.0.0.1:{self.server.server_port}/v1"
        state = core.refresh_models()
        self.assertEqual(state["engine"]["name"], "Ollama")
        self.assertEqual(state["engine"]["contextLength"], 32768)
        self.assertEqual(state["models"], ["qwen:test"])

    def test_bridge_rejects_external_urls(self):
        bridge = app.DesktopBridge(app.LinuxHostCore())
        response = bridge.rpc({"id": "unsafe", "action": "apiGet", "payload": {"path": "https://example.com"}})
        self.assertFalse(response["ok"])

    def test_reasoning_tags_are_streamed_separately_even_when_split(self):
        accumulator = app.ReasoningContentAccumulator()
        first = accumulator.append("<thi")
        second = accumulator.append("nk>أفكر")
        third = accumulator.append(" الآن</think>الجواب")
        self.assertEqual(first, ("", ""))
        self.assertEqual(second, ("", "أفكر"))
        self.assertEqual(third, ("الجواب", " الآن"))

    def test_unavailable_probes_do_not_break_unknown_engine_detection(self):
        original_try_json = app.try_json
        original_request_json = app.request_json
        try:
            app.try_json = lambda *_args, **_kwargs: None
            app.request_json = lambda *_args, **_kwargs: {"data": [{"id": "compatible-model", "context_length": 8192}]}
            core = app.LinuxHostCore()
            core.settings["api_url"] = "http://127.0.0.1:9999/v1"
            state = core.refresh_models()
            self.assertEqual(state["engine"]["name"], "OpenAI-compatible")
            self.assertEqual(state["engine"]["contextLength"], 8192)
        finally:
            app.try_json = original_try_json
            app.request_json = original_request_json

    def test_shared_ui_supports_both_native_bridges(self):
        script = (MODULE_PATH.parent.parent / "host-win-webview" / "ui" / "app.js").read_text(encoding="utf-8")
        self.assertIn("window.chrome?.webview", script)
        self.assertIn("window.pywebview?.api?.rpc", script)
        self.assertTrue((app.resolve_ui_dir() / "ai-palm-mark.png").is_file())


if __name__ == "__main__":
    unittest.main()

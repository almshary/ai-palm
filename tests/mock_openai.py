"""Tiny authenticated OpenAI-compatible server used for local integration tests."""

import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_args):
        pass

    def reply(self, payload, status=200):
        data = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def authorized(self):
        if self.headers.get("Authorization") != "Bearer test-key":
            self.reply({"error": "unauthorized"}, 401)
            return False
        return True

    def do_GET(self):  # noqa: N802
        if not self.authorized():
            return
        if self.path == "/v1/models":
            self.reply({"object": "list", "data": [{"id": "nawah-test-model", "object": "model"}]})
        else:
            self.reply({"error": "not found"}, 404)

    def do_POST(self):  # noqa: N802
        if not self.authorized():
            return
        if self.path != "/v1/chat/completions":
            self.reply({"error": "not found"}, 404)
            return
        length = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(length) or b"{}")
        prompt = body.get("messages", [{}])[-1].get("content", "")
        self.reply({"id": "chatcmpl-test", "choices": [{"message": {"role": "assistant", "content": f"نجح الطلب المحمي: {prompt}"}}]})


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", 1234), Handler).serve_forever()

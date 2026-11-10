"""Deterministic HTTP operator fixture for the local SM-6 Compose project."""
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from threading import Lock
from time import sleep
from uuid import UUID


attempts = {}
attempt_lock = Lock()


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200 if self.path == "/healthz" else 404)
        self.end_headers()

    def do_POST(self):
        if self.path != "/v1/email/send":
            self.send_error(404)
            return

        request = json.loads(self.read_body())
        scenario = request["message_text"]
        if scenario not in {"timeout", "mismatch"}:
            self.send_error(400)
            return
        delivery_id = str(UUID(request["delivery_id"]))
        with attempt_lock:
            attempt = attempts.get(delivery_id, 0) + 1
            attempts[delivery_id] = attempt

        if scenario == "timeout" and attempt == 1:
            response = None
        elif scenario == "mismatch" and attempt == 1:
            response = {"result": "accepted", "code": "SimulatedOperatorRejected"}
        else:
            response = {"result": "accepted", "code": "AcceptedByProvider"}

        status = None
        content_type = None
        if response is not None:
            status, content_type = 200, "application/json"
            body = json.dumps(response).encode()
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        path = Path("/control") / scenario / f"http-{attempt}.json"
        temporary = path.with_suffix(".tmp")
        temporary.write_text(json.dumps({
            "delivery_id": delivery_id, "status": status, "content_type": content_type, "response": response,
        }), encoding="utf-8")
        temporary.replace(path)
        if response is None:
            # Outlast the adapter's three-second deadline without sending headers.
            sleep(6)

    def log_message(self, *args):
        pass

    def read_body(self):
        if self.headers.get("Transfer-Encoding", "").lower() != "chunked":
            return self.rfile.read(int(self.headers["Content-Length"]))

        body = bytearray()
        while True:
            size = int(self.rfile.readline().strip().split(b";", 1)[0], 16)
            if size == 0:
                self.rfile.readline()
                return bytes(body)
            body.extend(self.rfile.read(size))
            self.rfile.read(2)  # CRLF after the chunk.


if __name__ == "__main__":
    ThreadingHTTPServer(("0.0.0.0", 8000), Handler).serve_forever()

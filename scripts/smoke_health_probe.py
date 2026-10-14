"""Execute the actual chiseled-container health helper against an isolated loopback server."""
import http.server
import pathlib
import subprocess
import sys
import threading

probe = pathlib.Path(sys.argv[1]).resolve()


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200 if self.path == "/health/ready" else 503 if self.path == "/unready" else 302)
        if self.path == "/redirect":
            self.send_header("Location", "/health/ready")
        self.end_headers()

    def log_message(self, *_):
        pass


server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
thread = threading.Thread(target=server.serve_forever, daemon=True)
thread.start()
base = f"http://127.0.0.1:{server.server_port}"
try:
    for url, expected in ((base + "/health/ready", 0), (base + "/unready", 1), (base + "/redirect", 1), ("https://example.invalid", 2)):
        result = subprocess.run(["dotnet", str(probe), url], check=False, timeout=5, capture_output=True)
        assert result.returncode == expected, (url, result.returncode, result.stderr.decode())
finally:
    server.shutdown()
    server.server_close()
result = subprocess.run(["dotnet", str(probe), base + "/health/ready"], check=False, timeout=5, capture_output=True)
assert result.returncode == 1
print("HealthProbe: 5 checks passed (ready, unready, redirect refused, non-loopback refused, connection refused).")

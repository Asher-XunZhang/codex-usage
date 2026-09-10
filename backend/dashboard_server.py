"""Serve the private Codex usage dashboard on 127.0.0.1 only."""
from __future__ import annotations
import argparse
import csv
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from http.client import HTTPException
import io
import json
import os
import sys
import subprocess
import time
import socket
import signal
from contextlib import nullcontext, contextmanager
from datetime import datetime, timezone
from pathlib import Path
import threading
from urllib.parse import parse_qs, urlsplit
from urllib.request import build_opener, ProxyHandler

from dashboard_data import UsageIndex


@contextmanager
def shutdown_signals(stop):
    """Signal handlers only set a flag; cleanup runs outside the handler."""
    previous = {}
    if threading.current_thread() is threading.main_thread():
        for sig in (signal.SIGINT, signal.SIGTERM):
            previous[sig] = signal.signal(sig, lambda *_: stop.set())
    try:
        yield
    finally:
        for sig, handler in previous.items():
            signal.signal(sig, handler)


def probe_health(url, expected_pid):
    try:
        with build_opener(ProxyHandler({})).open(url, timeout=2) as response:
            result = json.loads(response.read(4096))
        return (result.get("application") == "codex-token-usage-dashboard"
                and result.get("pid") == expected_pid
                and result.get("status") in ("healthy", "initializing"))
    except (OSError, ValueError, AttributeError, HTTPException):
        return False


def stop_worker(child):
    try:
        child.terminate()
    except ProcessLookupError:
        pass
    try:
        return child.wait(timeout=5)
    except subprocess.TimeoutExpired:
        child.kill()
        return child.wait()


def supervise(command, max_restarts=3, delay=5, health_url=None, job=None):
    stop = threading.Event()
    with shutdown_signals(stop):
        return supervise_until_stopped(command, stop, max_restarts, delay, health_url, job)


def supervise_until_stopped(command, stop, max_restarts, delay, health_url, job):
    for attempt in range(max_restarts + 1):
        if stop.is_set():
            return 0
        child = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=sys.stdout, stderr=sys.stderr)
        try:
            if job is not None:
                job.assign(child.pid)
            failed_probes = 0
            next_probe = time.monotonic() + 5
            while True:
                if stop.is_set():
                    stop_worker(child)
                    return 0
                try:
                    code = child.wait(timeout=0.25)
                    break
                except subprocess.TimeoutExpired:
                    if health_url and time.monotonic() >= next_probe:
                        next_probe = time.monotonic() + 5
                        failed_probes = 0 if probe_health(health_url, child.pid) else failed_probes + 1
                        if failed_probes >= 3:
                            print(json.dumps({"event": "health_check_failed", "pid": child.pid,
                                              "consecutive_failures": failed_probes,
                                              "at": datetime.now(timezone.utc).isoformat()}), flush=True)
                            code = stop_worker(child)
                            break
        except KeyboardInterrupt:
            stop_worker(child)
            return 0
        except BaseException:
            stop_worker(child)
            raise
        if stop.is_set():
            return 0
        print(json.dumps({"event": "worker_exited", "pid": child.pid, "code": code,
                          "attempt": attempt, "at": datetime.now(timezone.utc).isoformat()}), flush=True)
        if attempt < max_restarts:
            if stop.wait(delay):
                return 0
    print(json.dumps({"event": "restart_limit_reached", "at": datetime.now(timezone.utc).isoformat()}), flush=True)
    return 1


def make_handler(index, instance_id=None):
    assets = Path(__file__).resolve().parent / "dashboard"

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def send(self, code, body, mime):
            self.send_response(code)
            self.send_header("Content-Type", mime)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Referrer-Policy", "no-referrer")
            self.send_header("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'")
            self.end_headers()
            self.wfile.write(body)

        def local_request(self):
            port = self.server.server_port
            hosts = {f"127.0.0.1:{port}", f"localhost:{port}"}
            if self.headers.get("Host") not in hosts or (self.headers.get("Origin") and self.headers["Origin"] not in {f"http://{h}" for h in hosts}):
                self.send(403, b"Forbidden", "text/plain")
                return False
            return True

        def do_POST(self):
            if not self.local_request():
                return
            if not instance_id or self.headers.get("X-Codex-Instance") != instance_id:
                self.send(403, b"Forbidden", "text/plain")
                return
            try:
                length = int(self.headers.get("Content-Length", "0"))
                if not 0 < length <= 2048:
                    raise ValueError("Invalid length")
                body = json.loads(self.rfile.read(length))
                if not isinstance(body, dict):
                    raise ValueError("Invalid JSON")
                path = urlsplit(self.path).path
                if path == "/api/refresh":
                    wait_ms = body.get("wait_ms", 0)
                    if type(wait_ms) is not int or not 0 <= wait_ms <= 5000:
                        raise ValueError("Invalid wait")
                    ticket = index.request_refresh()
                    completed = index.wait_for_refresh(ticket, wait_ms / 1000) if wait_ms else index.refresh_completed
                    data = {"ticket": ticket, "completed": completed, "refresh_error": index.refresh_error,
                            "generated_at": index.updated}
                elif path == "/api/settings":
                    index.configure_refresh(body.get("refresh_seconds"))
                    data = {"refresh_seconds": index.refresh_seconds}
                else:
                    self.send(404, b"Not found", "text/plain")
                    return
            except (ValueError, TypeError):
                self.send(400, b'{"error":"Invalid request"}', "application/json")
                return
            self.send(200, json.dumps(data).encode(), "application/json")

        def do_GET(self):
            if not self.local_request():
                return
            url = urlsplit(self.path)
            if url.path == "/health":
                health = dict(application="codex-token-usage-dashboard", pid=os.getpid(), instance_id=instance_id, **index.health())
                self.send(503 if health["status"] == "stalled" else 200,
                          json.dumps(health).encode("utf-8"), "application/json")
                return
            if url.path in ("/api/usage", "/api/summary", "/api/export.csv"):
                try:
                    query = parse_qs(url.query)
                    filters = {key: query.get(key, [default])[0]
                               for key, default in (("days", "30"), ("model", "all"), ("task", "all"), ("group", "model"))}
                    try:
                        data = index.query(summary_only=url.path == "/api/summary", **filters)
                    except ValueError as exc:
                        # Floating summaries may outlive a deleted model/task.
                        # Return an explicit fallback; only that caller resets its
                        # own preferences. Main-page filtering remains unchanged.
                        if url.path != "/api/summary" or str(exc) not in ("Unknown model", "Unknown task"):
                            raise
                        filters.update(model="all", task="all")
                        data = index.query(summary_only=True, **filters)
                        data["filter_reset"] = True
                except ValueError:
                    self.send(400, b'{"error":"Invalid filters"}', "application/json")
                    return
                if url.path.endswith(".csv"):
                    stream = io.StringIO(newline="")
                    writer = csv.writer(stream)
                    writer.writerow(["范围", "输入", "其中缓存输入", "非缓存输入", "输出（含推理）", "总数", "模型调用"])
                    fields = ["input_tokens", "cached_input_tokens", "noncached_input_tokens", "output_tokens", "total_tokens", "requests"]
                    for row in data["groups"]:
                        label = row["label"]
                        if label.startswith(("=", "+", "-", "@", "\t", "\r")):
                            label = "'" + label
                        writer.writerow([label] + [row[k] if row[k] is not None else "不可统计" for k in fields])
                    self.send(200, stream.getvalue().encode("utf-8-sig"), "text/csv; charset=utf-8")
                else:
                    self.send(200, json.dumps(data, ensure_ascii=False).encode("utf-8"), "application/json; charset=utf-8")
                return
            routes = {"/": ("index.html", "text/html; charset=utf-8"),
                      "/app.js": ("app.js", "text/javascript; charset=utf-8"),
                      "/connection.js": ("connection.js", "text/javascript; charset=utf-8"),
                      "/styles.css": ("styles.css", "text/css; charset=utf-8")}
            if url.path not in routes:
                self.send(404, b"Not found", "text/plain")
                return
            name, mime = routes[url.path]
            try:
                self.send(200, (assets / name).read_bytes(), mime)
            except OSError:
                self.send(503, b"Dashboard assets are not ready", "text/plain")

    return Handler


class LocalHTTPServer(ThreadingHTTPServer):
    allow_reuse_address = os.name != 'nt'
    daemon_threads = True

    def get_request(self):
        connection, address = super().get_request()
        connection.settimeout(10)
        return connection, address

    def server_bind(self):
        if os.name == 'nt':
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()


def watch_parent(parent_pid, stop):
    """An app-owned worker must exit even when its native parent is force-quit."""
    while not stop.is_set():
        if os.getppid() != parent_pid:
            stop.set()
            return
        stop.wait(0.5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8766)
    parser.add_argument("--codex-home", type=Path, default=Path(os.environ.get("CODEX_HOME") or Path.home()/".codex"))
    parser.add_argument("--state-file", type=Path)
    parser.add_argument("--instance-id", help="Local service identity used by the macOS launcher")
    parser.add_argument("--log-file", type=Path, help="Append lifecycle and error output, including when run with pythonw")
    parser.add_argument("--supervise", action="store_true", help="Watch the server process; retry unexpected exits up to three times")
    parser.add_argument("--parent-pid", type=int, help="Exit when this direct parent exits (macOS desktop app)")
    parser.add_argument("--refresh-seconds", type=int, default=30, help="Seconds between scans; 0 disables automatic scans")
    parser.add_argument("--cache-path", type=Path, help="Private persistent SQLite index for the native desktop app")
    args = parser.parse_args()
    if args.parent_pid is not None and (args.parent_pid <= 1 or os.name == "nt" or args.supervise):
        parser.error("--parent-pid requires a direct Unix parent and cannot be combined with --supervise")
    if args.log_file:
        args.log_file.parent.mkdir(parents=True, exist_ok=True)
        sys.stdout = sys.stderr = args.log_file.open("a", encoding="utf-8", buffering=1)
    print(json.dumps({"event": "starting", "pid": os.getpid(), "at": datetime.now(timezone.utc).isoformat()}), flush=True)
    if args.supervise:
        command = [sys.executable, "-E", "-s", "-B", str(Path(__file__).resolve()),
                   *[arg for arg in sys.argv[1:] if arg != "--supervise"]]
        if args.port == 0:
            parser.error("--supervise requires a fixed --port for health checks")
        if os.name == 'nt':
            from windows_job import WindowsChildJob
            job_context = WindowsChildJob()
        else:
            job_context = nullcontext()
        with job_context as job:
            raise SystemExit(supervise(command, health_url=f"http://127.0.0.1:{args.port}/health", job=job))
    if not 0 <= args.port <= 65535:
        parser.error("--port must be between 0 and 65535")
    if not 0 <= args.refresh_seconds <= 3600:
        parser.error("--refresh-seconds must be between 0 and 3600")
    if args.cache_path:
        from disk_index import DiskUsageIndex
        index = DiskUsageIndex(args.codex_home.expanduser(), args.cache_path, refresh_seconds=args.refresh_seconds)
    else:
        index = UsageIndex(args.codex_home.expanduser(), refresh_seconds=args.refresh_seconds)
    if args.parent_pid is not None:
        threading.Thread(target=watch_parent, args=(args.parent_pid, index.stop), daemon=True).start()
    server = LocalHTTPServer(("127.0.0.1", args.port), make_handler(index, args.instance_id))
    threading.Thread(target=index.run, daemon=True).start()
    state = {"pid": os.getpid(), "url": f"http://127.0.0.1:{server.server_port}", "scope": "local-only"}
    if args.state_file:
        args.state_file.parent.mkdir(parents=True, exist_ok=True)
        args.state_file.write_text(json.dumps(state), encoding="utf-8")
    print(json.dumps(state), flush=True)
    server.timeout = 0.25
    try:
        with shutdown_signals(index.stop):
            while not index.stop.is_set():
                server.handle_request()
    finally:
        index.stop.set()
        server.server_close()
        if args.state_file:
            try:
                if json.loads(args.state_file.read_text()).get("pid") == os.getpid():
                    args.state_file.unlink()
            except (OSError, ValueError):
                pass
        print(json.dumps({"event": "stopped", "pid": os.getpid(), "at": datetime.now(timezone.utc).isoformat()}), flush=True)


if __name__ == "__main__":
    main()

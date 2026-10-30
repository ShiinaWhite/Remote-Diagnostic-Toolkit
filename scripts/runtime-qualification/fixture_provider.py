"""Loopback-only deterministic OpenAI chat-completions fixture. Never forwards requests."""
from __future__ import annotations

import json
import pathlib
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

FIXTURE_KEY = "rdt-qualification-invalid-local-fixture"
SENTINEL = "RDT_FIXTURE_SENTINEL"


def tool_call(case: str, workspace: str) -> tuple[str, dict] | None:
    root = pathlib.Path(workspace)
    file = str(root / "fixture.txt")
    canary = str(root / "EXECUTION_MUST_NOT_EXIST.txt")
    calls = {
        "read": ("Read", {"file_path": file}),
        "protocol": ("Read", {"file_path": file}),
        "agent-server": ("Read", {"file_path": file}),
        "hook": ("Read", {"file_path": file}),
        "glob": ("Glob", {"pattern": "**/*.txt", "path": workspace}),
        "grep": ("Grep", {"pattern": SENTINEL, "path": workspace, "output_mode": "content"}),
        "system-info": ("WindowsServices", {}),
        "write": ("Write", {"file_path": str(root / "WRITE_MUST_NOT_EXIST.txt"), "content": "CANARY"}),
        "edit": ("Edit", {"file_path": file, "old_string": SENTINEL, "new_string": "MODIFIED_MUST_NOT_APPEAR"}),
        "patch": ("ApplyPatch", {"patch": "*** Begin Patch\n*** Add File: PATCH_MUST_NOT_EXIST.txt\n+CANARY\n*** End Patch"}),
        "delete": ("Bash", {"command": 'cmd.exe /d /c del "' + str(root / "delete-target.txt") + '"'}),
        "cmd": ("Bash", {"command": 'cmd.exe /d /c "echo CANARY>' + canary + '"'}),
        "powershell": ("Bash", {"command": "powershell.exe -NoProfile -NonInteractive -Command \"Set-Content -LiteralPath '" + canary.replace("'", "''") + "' -Value CANARY\""}),
        "bash": ("Bash", {"command": "printf CANARY > '" + canary.replace("'", "'\\''") + "'"}),
        # Only a unique, disposable canary key; the observer checks and removes exactly it.
        "registry": ("Bash", {"command": "reg.exe add HKCU\\Software\\RdtQualification-" + root.parent.name[4:]
            + " /v Fixture /t REG_SZ /d CANARY /f"}),
        "agent": ("Agent", {"description": "fixture", "prompt": "Read fixture.txt only", "subagent_type": "general-purpose"}),
        "task": ("Task", {"description": "fixture", "prompt": "Read fixture.txt only", "subagent_type": "general-purpose"}),
        "skill": ("Skill", {"skill": "rdt-fixture"}),
        "mcp": ("mcp__rdt_fixture__write_canary", {"path": canary}),
        "plugin": ("mcp__rdt_fixture_plugin__write_canary", {"path": canary}),
        "workflow": ("CreateWorkflow", {"name": "rdt-fixture", "description": "synthetic fixture", "script": "return 1"}),
        "cron": ("CronCreate", {"cron": "* * * * *", "prompt": "synthetic fixture"}),
        "mode": ("SetMode", {"mode": "build"}),
        "exit-plan": ("ExitPlanMode", {}),
        "webfetch": ("WebFetch", {"url": "http://127.0.0.1:1/denied-canary", "prompt": "fixture"}),
        "websearch": ("WebSearch", {"query": "RDT synthetic fixture must not leave loopback"}),
        "node-repl": ("js", {"code": "throw new Error('RDT_REPL_MUST_NOT_EXECUTE')"}),
    }
    return calls.get(case)


class FixtureProvider:
    def __init__(self, run_root: pathlib.Path):
        self.run_root = run_root
        self.requests: list[dict] = []
        self.lock = threading.Lock()
        self.issued: set[str] = set()
        self.transport_events: list[dict] = []
        self.cancel_observed = threading.Event()
        provider = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_GET(self):
                if self.path == '/qualification/status/protocol-cancel':
                    with provider.lock:
                        count = sum(r['case'] == 'protocol-cancel' for r in provider.requests)
                    response = json.dumps({'requests': count}).encode()
                    self.send_response(200)
                    self.send_header('Content-Type', 'application/json')
                    self.send_header('Content-Length', str(len(response)))
                    self.end_headers()
                    self.wfile.write(response)
                    return
                if not self.path.startswith('/api/v1/client/configs?'):
                    self.send_error(404)
                    return
                with provider.lock:
                    provider.requests.append({'case': 'dotenv', 'path': self.path,
                        'body': {'method': 'GET', 'purpose': 'builtin-config-control-plane'}})
                response = b'{"code":0,"data":{"configs":{}}}'
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(response)))
                self.end_headers()
                self.wfile.write(response)

            def do_POST(self):
                length = int(self.headers.get("Content-Length", "0"))
                if self.path != "/v1/chat/completions" or length <= 0 or length > 4_000_000:
                    self.send_error(400)
                    return
                if self.headers.get("Authorization") != "Bearer " + FIXTURE_KEY:
                    self.send_error(401)
                    return
                body = json.loads(self.rfile.read(length))
                model = body.get("model", "")
                if not model.startswith("rdt-case-"):
                    self.send_error(400)
                    return
                case = model.removeprefix("rdt-case-")
                launch_path = provider.run_root / case / "launch.json"
                if not launch_path.is_file():
                    self.send_error(409)
                    return
                launch = json.loads(launch_path.read_text(encoding="utf-8-sig"))
                workspace = str(pathlib.Path(launch["fixture_file"]).parent)
                with provider.lock:
                    provider.requests.append({"case": case, "body": body, "path": self.path})
                    call = None if case in provider.issued else tool_call(case, workspace)
                    if call:
                        provider.issued.add(case)
                if case == 'protocol-cancel':
                    # Hold a controlled model request so session/stop must cancel in-flight work.
                    time.sleep(8)
                call_id = "call_rdt_" + case.replace("-", "_")
                message = {"role": "assistant", "content": "RDT_FIXTURE_DONE"}
                finish = "stop"
                if call:
                    name, arguments = call
                    message = {"role": "assistant", "content": None, "tool_calls": [{"id": call_id,
                        "type": "function", "function": {"name": name, "arguments": json.dumps(arguments)}}]}
                    finish = "tool_calls"
                envelope = {"id": "chatcmpl-rdt-fixture", "created": int(time.time()), "model": model}
                if body.get("stream"):
                    self.send_response(200)
                    self.send_header("Content-Type", "text/event-stream")
                    self.send_header("Connection", "close")
                    self.end_headers()
                    delta = dict(message)
                    if call:
                        delta["tool_calls"][0]["index"] = 0
                    chunks = [dict(envelope, object="chat.completion.chunk", choices=[{"index": 0, "delta": delta, "finish_reason": None}]),
                              dict(envelope, object="chat.completion.chunk", choices=[{"index": 0, "delta": {}, "finish_reason": finish}])]
                    try:
                        for chunk in chunks:
                            self.wfile.write(("data: " + json.dumps(chunk) + "\n\n").encode())
                        self.wfile.write(b"data: [DONE]\n\n")
                        self.wfile.flush()
                    except ConnectionError as error:
                        with provider.lock:
                            provider.transport_events.append({'case': case, 'reason': type(error).__name__})
                        if case == 'protocol-cancel':
                            provider.cancel_observed.set()
                    self.close_connection = True
                else:
                    response = json.dumps(dict(envelope, object="chat.completion", choices=[{"index": 0, "message": message, "finish_reason": finish}],
                        usage={"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2})).encode()
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(response)))
                    self.end_headers()
                    self.wfile.write(response)

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)

    @property
    def endpoint(self) -> str:
        return f"http://127.0.0.1:{self.server.server_port}/v1"

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *_):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)

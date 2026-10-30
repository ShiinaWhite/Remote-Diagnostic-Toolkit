"""Developer qualification driver; always returns PARTIAL until all coverage gaps close."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys

from fixture_provider import FixtureProvider, SENTINEL, tool_call

ROOT = pathlib.Path(__file__).resolve().parents[2]
RUNS = ROOT / "artifacts/runtime-qualification/runs"
IDENTITY = pathlib.Path(__file__).with_name("runtime-identity.json")
PINNED_IDENTITY = json.loads(IDENTITY.read_text(encoding='utf-8'))
HARNESS = ROOT / "tools/Rdt.Qualification/bin/Release/net10.0/Rdt.Qualification.dll"
CASES = ("version help doctor surface contract read glob grep system-info write edit patch delete cmd powershell bash registry "
         "agent task skill mcp plugin workflow cron mode exit-plan webfetch websearch node-repl hook stdin protocol agent-server protocol-cancel dotenv").split()
DENIAL = re.compile(r"not (?:found|available|registered|allowed)|unknown tool|disallow|denied|permission denied|NoSuchTool", re.I)


def profile_metadata(root: pathlib.Path) -> dict:
    """Metadata only; never read the operator's credentials or personal file contents."""
    entries, errors = {}, []
    if not root.exists():
        return {'exists': False, 'entries': {}, 'errors': []}
    pending = [root]
    while pending:
        path = pending.pop()
        try:
            info = path.lstat()
            reparse = bool(getattr(info, 'st_file_attributes', 0) & 0x400)
            entries[str(path.relative_to(root))] = {'size': info.st_size, 'mtime_ns': info.st_mtime_ns,
                                                   'reparse': reparse, 'directory': path.is_dir()}
            if len(entries) > 100_000:
                errors.append('METADATA_LIMIT')
                break
            if path.is_dir() and not reparse:
                pending.extend(path.iterdir())
        except OSError as error:
            errors.append(type(error).__name__)
    return {'exists': True, 'entries': entries, 'errors': errors}


def verify_identity(candidate: pathlib.Path, identity: dict) -> None:
    digest = identity.get("runtime_sha256")
    if (identity.get("production_enabled") is not False or identity.get("qualification_only") is not True
            or not isinstance(digest, str) or not re.fullmatch("[a-f0-9]{64}", digest)
            or not re.fullmatch("[a-f0-9]{40}", identity.get("upstream_commit", ""))
            or candidate.name != identity.get("runtime_filename")):
        raise ValueError("PINNED_QUALIFICATION_IDENTITY_REQUIRED")
    with candidate.open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != digest:
            raise ValueError("RUNTIME_HASH_MISMATCH")


def evaluate(case: str, result: dict, requests: list[dict], stdout: str) -> dict:
    advertised = sorted({tool["function"]["name"] for request in requests
        for tool in request["body"].get("tools", []) if tool.get("type") == "function"})
    outputs = [str(m.get("content", "")) for request in requests
        for m in request["body"].get("messages", []) if m.get("role") == "tool"]
    changed = result.get("workspace_before") != result.get("workspace_after")
    side_effect = changed or result.get("registry_canary_created", False)
    cleanup = result.get("session_absent") and result.get("cleanup") == "Complete"
    response = {"case": case, "status": "NOT_PROVEN", "advertised_tools": advertised,
                "provider_requests": len(requests), "tool_result_count": len(outputs),
                "workspace_changed": changed, "registry_side_effect": result.get("registry_canary_created", False),
                "cleanup_complete": cleanup, "runtime_exit": result.get("exit_code"),
                "timed_out": result.get("timed_out"), "observer_error_count": len(result.get("observer_errors", []))}
    if not cleanup or side_effect:
        response["status"] = "FAIL"
        return response
    if case == 'version':
        response['status'] = 'PASS' if result.get('exit_code') == 0 and stdout.strip() == PINNED_IDENTITY['cli_workspace_version'] else 'FAIL'
    elif case == 'doctor':
        try:
            doctor = json.loads(stdout)
            runtime = doctor.get('runtime', {})
            valid = (runtime.get('sea') is True and runtime.get('platform') == 'win32'
                and runtime.get('arch') == 'x64' and runtime.get('node') == 'v' + PINNED_IDENTITY['node_version']
                and doctor.get('cli', {}).get('version') == PINNED_IDENTITY['cli_workspace_version'])
            response['status'] = 'PASS' if result.get('exit_code') == 0 and valid else 'FAIL'
        except json.JSONDecodeError:
            response['status'] = 'FAIL'
    elif case == 'help':
        flags = ['--prompt','--cwd','--mode','--disallowed-tools','--workflow-mode','--json','--no-color']
        response['status'] = 'PASS' if result.get('exit_code') == 0 and all(flag in stdout for flag in flags) else 'FAIL'
    elif case in {"surface", "contract"}:
        response["status"] = "PASS" if advertised and "Read" in advertised else "NOT_PROVEN"
        if case == 'contract':
            try:
                response['single_json_output'] = isinstance(json.loads(stdout), dict)
            except json.JSONDecodeError:
                response['single_json_output'] = False
                response['status'] = 'FAIL'
    elif case == "dotenv":
        response["status"] = "ANCESTOR_ENV_LOADED" if any(r['body'].get('method') == 'GET' for r in requests) else "NOT_PROVEN"
    elif case == "hook":
        response["status"] = "DISABLED_CANARY" if outputs and any(SENTINEL in output for output in outputs) else "NOT_PROVEN"
    elif case in {"read", "glob", "grep"}:
        expected = SENTINEL if case in {"read", "grep"} else "fixture.txt"
        response["status"] = "PASS" if outputs and any(expected in output and not DENIAL.search(output) for output in outputs) else "NOT_PROVEN"
    elif case == "system-info":
        response["status"] = "UNAVAILABLE" if outputs and any(DENIAL.search(o) for o in outputs) else "NOT_PROVEN"
    elif case in {"protocol", "agent-server", "protocol-cancel"}:
        protocol = result.get("protocol_result") or {}
        response["protocol_result"] = protocol
        if protocol.get("completed") and any(SENTINEL in output for output in outputs):
            response["status"] = "PASS_REQUEST"
        if case == 'protocol-cancel' and protocol.get('stop_acknowledged'):
            response['status'] = 'STOP_ACKNOWLEDGED'
            response['fixture_transport_abort'] = result.get('fixture_transport_abort', False)
            if response['fixture_transport_abort']:
                response['status'] = 'CANCEL_OBSERVED'
    elif case not in {"stdin", "protocol", "agent-server"}:
        response["status"] = "DENIED" if outputs and all(DENIAL.search(output) for output in outputs) else "NOT_PROVEN"
    if result.get("timed_out") or result.get("observer_errors"):
        response["status"] = "NOT_PROVEN"
    return response


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", type=pathlib.Path, required=True)
    parser.add_argument("--cases", nargs="+", choices=CASES, default=CASES)
    options = parser.parse_args()
    identity = json.loads(IDENTITY.read_text(encoding="utf-8"))
    verify_identity(options.candidate, identity)
    if sys.platform != "win32":
        raise SystemExit("WINDOWS_QUALIFICATION_HOST_REQUIRED")
    RUNS.mkdir(parents=True, exist_ok=True)
    real_zcode = pathlib.Path(os.environ['USERPROFILE']) / '.zcode'
    metadata_before = profile_metadata(real_zcode)
    observations = []
    with FixtureProvider(RUNS) as provider:
        for case in options.cases:
            case_path = RUNS / case
            case_path.mkdir(exist_ok=True)
            # Never mistake an earlier run's result for a bridge that failed before writing.
            (case_path / 'result.json').unlink(missing_ok=True)
            # Only the fixed .NET qualification bridge can launch the pinned candidate.
            command = ["dotnet", str(HARNESS), str(options.candidate.resolve()), case, provider.endpoint]
            with subprocess.Popen(command, cwd=ROOT, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                  stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace") as run:
                try:
                    bridge_out, bridge_err = run.communicate(timeout=65)
                except subprocess.TimeoutExpired:
                    # Kill this development bridge's whole process tree, not only its parent.
                    subprocess.run([str(pathlib.Path(os.environ['SystemRoot'])/'System32/taskkill.exe'),
                                    '/PID', str(run.pid), '/T', '/F'], capture_output=True, check=False)
                    bridge_out, bridge_err = run.communicate(timeout=10)
            (case_path / "bridge-stdout.txt").write_text(bridge_out, encoding="utf-8")
            (case_path / "bridge-stderr.txt").write_text(bridge_err, encoding="utf-8")
            result_path = case_path / "result.json"
            if not result_path.is_file():
                observation = {"case": case, "status": "NOT_PROVEN", "bridge_exit": run.returncode}
            else:
                result = json.loads(result_path.read_text(encoding="utf-8-sig"))
                if case == 'protocol-cancel':
                    result['fixture_transport_abort'] = provider.cancel_observed.wait(timeout=9)
                    result_path.write_text(json.dumps(result), encoding='utf-8')
                stdout_path = case_path / "stdout.txt"
                stdout = stdout_path.read_text(encoding="utf-8-sig") if stdout_path.is_file() else ""
                requests = [r for r in provider.requests if r["case"] == case]
                observation = evaluate(case, result, requests, stdout)
                (case_path / "provider-requests.json").write_text(json.dumps(requests, indent=2), encoding="utf-8")
            observations.append(observation)
            print(json.dumps(observation), flush=True)
    summary = {"status": "PARTIAL", "runtime_sha256": identity["runtime_sha256"],
               "cases": observations, "real_api_used": False, "real_runtime_enabled": False,
               "coverage_gaps": ["Non-admin TCP/process sampling is not a complete network audit",
                   "Filesystem events require attribution; full machine image/VM diff not available",
                   "Configured hostile extension positive controls and protocol cancellation need explicit qualification",
                   "Windows operations beyond filesystem reads need architecture review"]}
    metadata_after = profile_metadata(real_zcode)
    metadata_diff = [name for name in metadata_before['entries'].keys() | metadata_after['entries'].keys()
                     if metadata_before['entries'].get(name) != metadata_after['entries'].get(name)]
    full_campaign = set(options.cases) == set(CASES)
    metadata_name = 'real-zcode-metadata.json' if full_campaign else 'real-zcode-subset-metadata.json'
    (RUNS/metadata_name).write_text(json.dumps({'before': metadata_before, 'after': metadata_after,
        'changed_relative_paths': metadata_diff}, indent=2), encoding='utf-8')
    summary['real_zcode_metadata'] = {'changed_entries': len(metadata_diff),
        'before_entries': len(metadata_before['entries']), 'after_entries': len(metadata_after['entries']),
        'errors': metadata_before['errors'] + metadata_after['errors'], 'file_contents_read': False}
    summary['selected_cases'] = options.cases
    summary_name = 'summary.json' if full_campaign else 'subset-summary.json'
    (RUNS / summary_name).write_text(json.dumps(summary, indent=2), encoding="utf-8")
    return 1 if any(r["status"] == "FAIL" for r in observations) else 0


if __name__ == "__main__":
    raise SystemExit(main())

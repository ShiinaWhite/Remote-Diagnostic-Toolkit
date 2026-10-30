"""Run fixed developer YOLO experiments, preserving Phase 1 evidence and production gate."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import re
import subprocess
import time

from qualify import ROOT, profile_metadata, verify_identity
from yolo_fixture import CASES, YoloFixture

RUNS = ROOT/'artifacts/runtime-qualification/yolo/runs'
HARNESS = ROOT/'tools/Rdt.YoloQualification/bin/Release/net10.0/Rdt.YoloQualification.dll'
IDENTITY = pathlib.Path(__file__).with_name('yolo-runtime-identity.json')


def outputs(requests: list[dict]) -> list[str]:
    # Dedupe successive model requests carrying the same complete conversation.
    found = {}
    for request in requests:
        for message in request['body'].get('messages',[]):
            if message.get('role') == 'tool':
                found[message.get('tool_call_id',str(len(found)))] = str(message.get('content',''))
    return list(found.values())


def powershell_results(tool_outputs: list[str]) -> list[dict]:
    result = []
    for output in tool_outputs:
        for match in re.finditer('RDT_YOLO_RESULT:',output):
            try:
                value, _ = json.JSONDecoder().raw_decode(output[match.end():].lstrip())
                result.append(value)
            except (ValueError,TypeError):
                pass
    return result


def permission_mode(result: dict) -> str | None:
    protocol = result.get('protocol_result') or {}
    for key in ('initial_state','state','final_state','create_response'):
        mode = protocol.get(key,{}).get('result',{}).get('settings',{}).get('permission',{}).get('mode')
        if isinstance(mode,str): return mode
    return None


def evaluate(case: str, result: dict, requests: list[dict], before: dict, after: dict) -> dict:
    tools = sorted({t['function']['name'] for r in requests for t in r['body'].get('tools',[])})
    tool_outputs = outputs(requests)
    ps = powershell_results(tool_outputs)
    actual = next((p for p in ps if p.get('case') == case),{})
    lifecycle = result.get('children') or {}
    protocol = result.get('protocol_result') or {}
    clean = result.get('cleanup') == 'Complete' and result.get('session_absent') and result.get('registry_absent_final')
    status = 'NOT_PROVEN'
    if case in {'surface','local-builtin-idle'}:
        status = 'PASS' if tools and protocol.get('completed') else 'NOT_PROVEN'
    elif case == 'read':
        status = 'PASS' if any('RDT_YOLO_SENTINEL' in v for v in tool_outputs) else 'NOT_PROVEN'
    elif case == 'write':
        expected = hashlib.sha256(b'RDT_WRITTEN_SENTINEL\n').hexdigest()
        status = 'PASS' if after.get('written.txt') == expected and any('created successfully' in v.lower() for v in tool_outputs) else 'NOT_PROVEN'
    elif case == 'edit':
        expected = hashlib.sha256(b'RDT_AFTER_EDIT\n').hexdigest()
        status = 'PASS' if after.get('edit-target.txt') == expected and len(tool_outputs) >= 2 else 'NOT_PROVEN'
    elif case in {'glob','grep','patch'}:
        status = 'UNAVAILABLE' if any(re.search(r'not found|unknown tool|NoSuchTool',v,re.I) for v in tool_outputs) else 'NOT_PROVEN'
    elif case == 'model-cancel':
        status = 'STOP_ACKNOWLEDGED' if protocol.get('target_observed') and protocol.get('stop_ack') else 'NOT_PROVEN'
    elif case in {'tool-stop','wrapper-cancel'}:
        status = 'PASS' if protocol.get('target_observed') and protocol.get('stop_ack') and lifecycle.get('markers') and not lifecycle.get('recovery_required') else 'NOT_PROVEN'
        if case == 'wrapper-cancel' and not protocol.get('wrapper_token_cancelled'):
            status = 'NOT_PROVEN'
    elif case == 'tool-failure':
        status = 'PASS' if actual.get('ok') is False and any('RDT_CONTROLLED_FAILURE' in v and 'Exit code 7' in v for v in tool_outputs) else 'NOT_PROVEN'
    elif actual.get('ok'):
        status = 'PASS'
        data = actual.get('data',[])
        if case in {'services','processes','eventlog','os','network','scheduled','registry-read'} and not data:
            status = 'NOT_PROVEN'
        if case == 'registry-read' and (not data or data[0].get('Fixture') != 'RDT_REGISTRY_SENTINEL'):
            status = 'NOT_PROVEN'
        if case == 'registry-lifecycle' and (not data or data[0] != {'created_read':'RDT_CREATED','modified_read':'RDT_MODIFIED','deleted':True}
                or not result.get('registry_absent_before_recovery')):
            status = 'FAIL'
        if case == 'parent-dotenv' and (not data or data[0].get('canary') is not None or data[0].get('base_url') is not None):
            status = 'FAIL'
        if case == 'delete' and ('delete-target.txt' in after or 'delete-target.txt' not in before):
            status = 'FAIL'
        if case == 'subprocess' and (not data or data[0].get('exit') != 0 or data[0].get('output') != 'RDT_CHILD_SENTINEL'):
            status = 'FAIL'
        if case == 'mkdir' and (not data or data[0].get('exists') is not True):
            status = 'NOT_PROVEN'
        if case == 'privilege' and (not data or not isinstance(data[0].get('query_exit'),int) or not data[0].get('query_output')):
            status = 'NOT_PROVEN'
    if not clean or lifecycle.get('recovery_required'):
        status = 'FAIL'
    elif result.get('errors') or result.get('forced_kill') or result.get('runtime_exit') != 0:
        status = 'NOT_PROVEN'
    elif permission_mode(result) != 'yolo':
        status = 'NOT_PROVEN'
    return {'case':case,'status':status,'advertised_tools':tools,'provider_requests':len(requests),
        'tool_results':len(tool_outputs),'powershell_ok':actual.get('ok'),'powershell_rows':actual.get('count'),
        'cleanup_complete':bool(clean),'child_recovery_required':lifecycle.get('recovery_required'),
        'child_marker_count':len(lifecycle.get('markers',[])),'runtime_exit':result.get('runtime_exit'),
        'forced_kill':result.get('forced_kill'),'observer_error_count':len(result.get('errors',[])),
        'registry_absent_before_recovery':result.get('registry_absent_before_recovery'),
        'actual_permission_mode':permission_mode(result)}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--candidate',type=pathlib.Path,required=True)
    parser.add_argument('--builtin',type=pathlib.Path,required=True)
    parser.add_argument('--cases',nargs='+',choices=CASES,default=CASES)
    parser.add_argument('--log',type=pathlib.Path)
    options = parser.parse_args()
    if options.log:
        import sys
        log_path = options.log.resolve()
        if not log_path.is_relative_to((ROOT/'artifacts/runtime-qualification').resolve()):
            raise SystemExit('LOG_MUST_BE_IN_PRIVATE_QUALIFICATION_ARTIFACTS')
        log_path.parent.mkdir(parents=True,exist_ok=True)
        sys.stdout = log_path.open('w',encoding='utf-8',buffering=1)
        sys.stderr = sys.stdout
    identity = json.loads(IDENTITY.read_text(encoding='utf-8'))
    verify_identity(options.candidate,identity)
    if hashlib.sha256(options.builtin.read_bytes()).hexdigest() != identity['builtin_sha256']:
        raise SystemExit('BUILTIN_HASH_MISMATCH')
    RUNS.mkdir(parents=True,exist_ok=True)
    real_profile = pathlib.Path(os.environ['USERPROFILE'])/'.zcode'
    real_before = profile_metadata(real_profile)
    observations = []
    with YoloFixture(RUNS) as fixture:
        for case in options.cases:
            target = RUNS/case; target.mkdir(exist_ok=True)
            for name in ('result.json','exception.txt'):
                (target/name).unlink(missing_ok=True)
            get_start = len(fixture.gets)
            command = ['dotnet',str(HARNESS),str(options.candidate.resolve()),case,fixture.endpoint,str(options.builtin.resolve())]
            with subprocess.Popen(command,cwd=ROOT,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.PIPE,
                                  text=True,encoding='utf-8',errors='replace') as run:
                try:
                    out,err = run.communicate(timeout=155)
                except subprocess.TimeoutExpired:
                    subprocess.run([str(pathlib.Path(os.environ['SystemRoot'])/'System32/taskkill.exe'),'/PID',str(run.pid),'/T','/F'],capture_output=True,check=False)
                    out,err = run.communicate(timeout=10)
            (target/'bridge-stdout.txt').write_text(out,encoding='utf-8')
            (target/'bridge-stderr.txt').write_text(err,encoding='utf-8')
            selected = [r for r in fixture.requests if r['case'] == case]
            (target/'provider-requests.json').write_text(json.dumps(selected,indent=2),encoding='utf-8')
            if (target/'result.json').is_file():
                result = json.loads((target/'result.json').read_text(encoding='utf-8-sig'))
                before = json.loads((target/'workspace-before.json').read_text(encoding='utf-8-sig'))
                after_file = target/'workspace-after.json'
                after = json.loads(after_file.read_text(encoding='utf-8-sig')) if after_file.exists() else {}
                verdict = evaluate(case,result,selected,before,after)
                if case == 'model-cancel':
                    time.sleep(9)
                    aborted = any(e['case'] == case for e in fixture.transport)
                    verdict['fixture_transport_aborted'] = aborted
                    if aborted and verdict['status'] == 'STOP_ACKNOWLEDGED': verdict['status'] = 'PASS'
            else:
                verdict = {'case':case,'status':'NOT_PROVEN','bridge_exit':run.returncode}
            verdict['control_plane_gets'] = len(fixture.gets)-get_start
            if verdict['control_plane_gets'] and case in {'parent-dotenv','local-builtin-idle'}: verdict['status'] = 'FAIL'
            (target/'verdict.json').write_text(json.dumps(verdict,indent=2),encoding='utf-8')
            observations.append(verdict)
            print(json.dumps(verdict),flush=True)
        (RUNS.parent/'network-canaries.json').write_text(json.dumps({'gets':fixture.gets,'transport':fixture.transport}),encoding='utf-8')
    real_after = profile_metadata(real_profile)
    profile = {'unchanged':real_before == real_after,'before':real_before,'after':real_after}
    (RUNS.parent/'profile-metadata.json').write_text(json.dumps(profile),encoding='utf-8')
    if options.cases == CASES:
        (RUNS.parent/'full-campaign-profile.json').write_text(json.dumps(profile),encoding='utf-8')
    (RUNS.parent/'last-run.json').write_text(json.dumps({'cases':observations,'real_zcode_unchanged':real_before == real_after},indent=2),encoding='utf-8')
    print(json.dumps({'real_zcode_unchanged':real_before == real_after,'cases':len(observations)}),flush=True)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())

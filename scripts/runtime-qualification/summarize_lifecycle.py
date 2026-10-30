"""Allowlisted public lifecycle evidence. No raw paths, PID, command, model IO or credentials."""
import json
import pathlib

from qualify import ROOT
from qualify_lifecycle import RUNS,evaluate
from lifecycle_fixture import CASES,STRINGS


def summarize():
    cases=[]
    for name in CASES:
        folder=RUNS/name
        if not (folder/'result.json').exists():
            cases.append({'case':name,'status':'NOT_RUN'}); continue
        raw=json.loads((folder/'result.json').read_text(encoding='utf-8-sig'))
        requests=json.loads((folder/'provider-requests.json').read_text(encoding='utf-8'))
        snapshot=folder/'workspace-after.json'
        verdict=evaluate(name,raw,requests,json.loads(snapshot.read_text(encoding='utf-8-sig')) if snapshot.exists() else {})
        members=raw.get('memberships') or {}
        cases.append(dict(verdict,job_created=raw.get('created_job'),job_assigned=raw.get('assigned'),
            zcode_job_membership=raw.get('root_member'),job_limit_flags=raw.get('limit_flags'),job_handle_inheritable=raw.get('handle_inheritable'),
            wrapper_already_in_job=raw.get('wrapper_in_job'),runtime_preassign_in_job=raw.get('runtime_preassign_in_job'),
            assign_ms=raw.get('assign_ms'),protocol_requests=raw.get('protocol_requests'),native_error_code=raw.get('native_error_code'),
            powershell_job_membership=members.get('active.pid.json',{}).get('member'),
            detached_job_membership=members.get('detached.pid.json',{}).get('member'),
            hold_tool_job_membership=members.get('hold.pid.json',{}).get('member'),
            preassign_system_console_hosts=sum(bool(p.get('windows_console_host')) for p in raw.get('startup_processes',[])),
            preassign_nonmember_console_hosts=sum(bool(p.get('windows_console_host')) and not p.get('member_after_root_assign') for p in raw.get('startup_processes',[])),
            startup_exited_without_recovery=raw.get('startup_exited_without_recovery'),
            observed_process_handles=(raw.get('after_close') or {}).get('observed_handles'),
            all_process_handles_exited=(raw.get('after_close') or {}).get('all_exited'),
            pid_recovery_required=(raw.get('recovery') or {}).get('recovery_required'),
            runtime_exit=raw.get('runtime_exit'),wrapper_force_killed=raw.get('wrapper_force_killed',False),
            parent_session_cleanup=raw.get('parent_session_cleanup',False),session_cleanup=raw.get('cleanup'),session_absent=raw.get('session_absent')))
    return {'schema_version':1,'scope':'Windows Job lifetime and UTF-8 only; production adapter remains disabled',
        'runtime_sha256':json.loads(pathlib.Path(__file__).with_name('yolo-runtime-identity.json').read_text())['runtime_sha256'],
        'status':'PASS' if all(c['status']=='PASS' for c in cases) else 'PARTIAL','cases':cases,
        'unicode_expected':STRINGS,'transport':'agent-server --stdio','permission_mode':'yolo','real_api_used':False,'production_runtime_enabled':False,
        'non_blocking_historical_items':['NotSigned','0.0.0-dev metadata','ordinary-user capability not qualified','whole-machine filesystem attribution'],
        'limits':['Observed and approved fixed SEA only; no guarantee for a future hash or every enclosing Job policy',
            'Windows console host observed outside RDT Job; trusted system image exits with root, checked separately',
            'Forced wrapper termination kills Job members; Session directory removal needs an external validated cleaner',
            'Job owns normal descendants; not a command sandbox or undo of filesystem/registry/service changes',
            'Unicode contract requires the explicit preamble and UTF-8 file encoding; no post-processing repair']}


if __name__=='__main__':
    destination=ROOT/'docs/runtime-qualification/lifecycle-utf8-evidence-summary.json'
    destination.write_text(json.dumps(summarize(),ensure_ascii=False,indent=2)+'\n',encoding='utf-8',newline='\n')
    print(str(destination.relative_to(ROOT)))

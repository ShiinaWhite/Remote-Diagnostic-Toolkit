"""Fixed SEA Job Object + Unicode qualification; no production runtime or real API."""
from __future__ import annotations
import argparse
import ctypes as C
from ctypes import wintypes as W
import hashlib
import json
import pathlib
import subprocess
import time

from qualify import ROOT, verify_identity
from qualify_yolo import outputs, powershell_results, permission_mode
from yolo_fixture import YoloFixture
from lifecycle_fixture import CASES, STRINGS, UNICODE_PATH, calls

RUNS = ROOT/'artifacts/runtime-qualification/lifecycle/runs'
HARNESS = ROOT/'tools/Rdt.LifecycleQualification/bin/Release/net10.0/Rdt.LifecycleQualification.dll'
IDENTITY = pathlib.Path(__file__).with_name('yolo-runtime-identity.json')


def evaluate(case: str, result: dict, requests: list[dict], workspace: dict) -> dict:
    tool_outputs = outputs(requests)
    ps = next((r for r in powershell_results(tool_outputs) if r.get('case') == case),{})
    members = result.get('memberships') or {}
    clean = result.get('cleanup') == 'Complete' and result.get('session_absent') is True
    recovery = result.get('recovery') or {}
    closed = result.get('after_close') or {}
    bound = (result.get('created_job') and result.get('assigned') and result.get('root_member')
        and result.get('limit_flags') == 0x2000 and result.get('handle_inheritable') is False)
    checks = {'job_bound':bool(bound),'session_cleanup':clean,
        'kernel_process_cleanup':closed.get('all_exited') is True,'no_pid_recovery':recovery.get('recovery_required') is False,
        'no_preassign_escape':result.get('startup_exited_without_recovery') is True,
        'known_preassign_processes_only':all(p.get('windows_console_host') or p.get('member_after_root_assign') for p in result.get('startup_processes',[])),
        'no_observer_errors':not result.get('errors')}
    if case.startswith('inject-'):
        stage = case.split('-')[1]
        checks = {'fail_closed':result.get('assigned') is False and result.get('protocol_requests') == 0 and not requests,
            'exact_injected_failure':result.get('errors') == ['INJECTED_JOB_'+stage.upper()+'_FAILURE'],
            'session_cleanup':clean,'root_stopped':stage != 'assign' or result.get('unassigned_stopped') is True}
    elif case == 'startup-race':
        checks['no_protocol_before_assign'] = result.get('protocol_requests') == 0 and not requests
    else:
        checks['yolo_permission'] = permission_mode(result) == 'yolo'
        surface = {t['function']['name'] for r in requests for t in r['body'].get('tools',[])}
        checks['normal_core_surface'] = {'Bash','Read','Write','Edit'}.issubset(surface)
        protocol = result.get('protocol_result') or {}
        if case not in {'model-cancel','write','edit'}:
            checks['powershell_membership'] = members.get('active.pid.json',{}).get('member') is True
        if case in {'detached-child','detached-stop','detached-wrapper-cancel','runtime-error','wrapper-killed'}:
            checks['detached_membership'] = members.get('detached.pid.json',{}).get('member') is True
        if case in {'tool-cancel','wrapper-cancel','session-stop','detached-stop','detached-wrapper-cancel','model-cancel'}:
            checks['stop_ack'] = protocol.get('stop_ack') is True
        if case in {'wrapper-cancel','detached-wrapper-cancel'}:
            checks['actual_wrapper_token_cancelled'] = protocol.get('wrapper_token_cancelled') is True
        if case == 'model-cancel': checks['fixture_connection_aborted'] = result.get('fixture_transport_aborted') is True
        if case == 'wrapper-cleanup': checks['wrapper_cleanup'] = protocol.get('explicit_wrapper_cleanup') is True
        if case == 'runtime-error':
            checks['controlled_runtime_fault'] = protocol.get('expected_runtime_fault') is True and result.get('runtime_exit') not in (None,0)
        elif case not in {'wrapper-cleanup','wrapper-killed'}: checks['normal_runtime_exit'] = result.get('runtime_exit') == 0
        if case == 'wrapper-killed': checks['actual_owner_kill'] = result.get('wrapper_force_killed') is True and result.get('owner_kill_process_handles_verified') is True
        if case in {'normal','session-stop','nested-job','detached-child','detached-stop','wrapper-killed'}:
            checks['agent_tool_completed'] = ps.get('ok') is True and protocol.get('completed') is True
        if case=='detached-wrapper-cancel':
            checks['detached_parent_tool_completed'] = ps.get('ok') is True and members.get('hold.pid.json',{}).get('member') is True
        if case in {'detached-child','detached-stop','detached-wrapper-cancel'}:
            checks['detached_alive_before_job_close'] = members.get('detached.pid.json',{}).get('pid') in {p['pid'] for p in result.get('live_before_close',[])}
        if case == 'tool-failure': checks['actual_tool_failure'] = ps.get('ok') is False and any('Exit code 7' in text and 'RDT_CONTROLLED_FAILURE' in text for text in tool_outputs)
        if case in {'write','edit'}:
            filename = 'written.txt' if case == 'write' else 'edit-target.txt'
            expected = b'RDT_WRITTEN_SENTINEL\n' if case == 'write' else b'RDT_AFTER_EDIT\n'
            checks['actual_file_changed'] = workspace.get(filename) == hashlib.sha256(expected).hexdigest() and bool(tool_outputs)
        if case.startswith('unicode-'):
            data = (ps.get('data') or [{}])[0]
            direct=[line.removeprefix('RDT_UNICODE_DIRECT:') for text in tool_outputs for line in text.splitlines() if line.startswith('RDT_UNICODE_DIRECT:')]
            json_values=[json.loads(line.removeprefix('RDT_UNICODE_JSON:')) for text in tool_outputs for line in text.splitlines() if line.startswith('RDT_UNICODE_JSON:')]
            checks['unicode_powershell'] = direct == STRINGS and data.get('strings') == STRINGS
            checks['unicode_json'] = json_values == [{'strings':STRINGS}] and data.get('json',{}).get('strings') == STRINGS
            expected = '\n'.join(STRINGS)
            checks['unicode_file'] = data.get('file') == expected and workspace.get(UNICODE_PATH.replace('/', '\\')) == hashlib.sha256(expected.encode('utf-8')).hexdigest()
            checks['unicode_path'] = data.get('relative_path') == UNICODE_PATH and data.get('leaf') == '中文诊断.txt'
            checks['builtin_read_unicode'] = len(tool_outputs) >= 2 and all(value in tool_outputs[-1] for value in STRINGS)
            checks['no_replacement_character'] = all('\ufffd' not in text for text in tool_outputs)
            checks['actual_ps_version'] = str(data.get('version','')).startswith('5.1.' if case == 'unicode-ps51' else '7.')
    return {'case':case,'status':'PASS' if all(checks.values()) else 'FAIL','checks':checks,
        'provider_requests':len(requests),'tool_results':len(tool_outputs),'actual_permission_mode':permission_mode(result),
        'powershell_version':((ps.get('data') or [{}])[0].get('version') if ps.get('ok') else None)}


def retain_processes(rows: list[dict]):
    kernel = C.WinDLL('kernel32',use_last_error=True)
    kernel.OpenProcess.argtypes = [W.DWORD,W.BOOL,W.DWORD]; kernel.OpenProcess.restype = W.HANDLE
    kernel.GetProcessTimes.argtypes = [W.HANDLE,C.POINTER(W.FILETIME),C.POINTER(W.FILETIME),C.POINTER(W.FILETIME),C.POINTER(W.FILETIME)]
    kernel.WaitForSingleObject.argtypes = [W.HANDLE,W.DWORD]; kernel.CloseHandle.argtypes = [W.HANDLE]
    handles = []
    try:
        for row in rows:
            handle = kernel.OpenProcess(0x101001,False,row['pid'])
            if not handle: raise C.WinError(C.get_last_error())
            handles.append(handle)
            creation,exit,kernel_time,user_time = W.FILETIME(),W.FILETIME(),W.FILETIME(),W.FILETIME()
            if not kernel.GetProcessTimes(handle,C.byref(creation),C.byref(exit),C.byref(kernel_time),C.byref(user_time)):
                raise C.WinError(C.get_last_error())
            ticks = ((creation.dwHighDateTime<<32)|creation.dwLowDateTime)+504911232000000000
            if ticks != row['start_ticks']: raise ValueError('PID_REUSE_BEFORE_WRAPPER_KILL')
        return kernel,handles
    except BaseException:
        for handle in handles: kernel.CloseHandle(handle)
        raise


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--candidate',type=pathlib.Path,required=True)
    parser.add_argument('--builtin',type=pathlib.Path,required=True)
    parser.add_argument('--cases',nargs='+',choices=CASES,default=CASES)
    options = parser.parse_args()
    pin = json.loads(IDENTITY.read_text())
    verify_identity(options.candidate,pin)
    if hashlib.sha256(options.builtin.read_bytes()).hexdigest() != pin['builtin_sha256']: raise SystemExit('BUILTIN_HASH_MISMATCH')
    RUNS.mkdir(parents=True,exist_ok=True)
    with YoloFixture(RUNS,cases=CASES,scenario=calls) as fixture:
        for case in options.cases:
            folder=RUNS/case; folder.mkdir(exist_ok=True)
            for name in ('result.json','wrapper-kill-ready.json','exception.txt'): (folder/name).unlink(missing_ok=True)
            command=['dotnet',str(HARNESS),str(options.candidate.resolve()),case,fixture.endpoint,str(options.builtin.resolve())]
            with subprocess.Popen(command,cwd=ROOT,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.PIPE,
                                  text=True,encoding='utf-8',errors='strict') as run:
                if case == 'wrapper-killed':
                    ready_file=folder/'wrapper-kill-ready.json'
                    until=time.monotonic()+85
                    while not ready_file.exists() and run.poll() is None and time.monotonic()<until: time.sleep(.05)
                    if not ready_file.exists(): raise RuntimeError('WRAPPER_KILL_NOT_READY')
                    ready=json.loads(ready_file.read_text(encoding='utf-8-sig'))
                    rows=list(ready['live'])
                    rows.extend(row for row in ready['startup_processes'] if row['pid'] not in {p['pid'] for p in rows})
                    kernel,handles=retain_processes(rows)
                    try:
                        launch=json.loads((folder/'launch.json').read_text(encoding='utf-8-sig'))
                        critical={launch['runtime_pid'],ready['memberships']['detached.pid.json']['pid']}
                        alive_before_kill=all(kernel.WaitForSingleObject(h,0)==258 for row,h in zip(rows,handles) if row['pid'] in critical)
                        run.kill() # Terminate ONLY the wrapper, not a tree; OS closes its last Job handle.
                        out,err=run.communicate(timeout=15)
                        waits=[kernel.WaitForSingleObject(h,8000) for h in handles]
                        exited=bool(handles) and all(value==0 for value in waits)
                        recovered=False
                        kernel.TerminateProcess.argtypes=[W.HANDLE,W.UINT]
                        for handle,value in zip(handles,waits):
                            if value!=0:
                                recovered=True
                                if not kernel.TerminateProcess(handle,125): raise C.WinError(C.get_last_error())
                                if kernel.WaitForSingleObject(handle,8000)!=0: raise RuntimeError('OWNED_PROCESS_RECOVERY_FAILED')
                        kernel.GetExitCodeProcess.argtypes=[W.HANDLE,C.POINTER(W.DWORD)]
                        exit_code=W.DWORD()
                        root_handle=next(handle for row,handle in zip(rows,handles) if row['pid']==launch['runtime_pid'])
                        if not kernel.GetExitCodeProcess(root_handle,C.byref(exit_code)): raise C.WinError(C.get_last_error())
                        runtime_exit=C.c_int(exit_code.value).value
                    finally:
                        for handle in handles: kernel.CloseHandle(handle)
                    recovery=subprocess.run(['dotnet',str(HARNESS),'recover-killed-wrapper'],cwd=ROOT,capture_output=True,text=True,timeout=15)
                    parent_cleanup=json.loads((folder/'parent-cleanup.json').read_text(encoding='utf-8-sig')) if (folder/'parent-cleanup.json').exists() else {}
                    result={'case_name':case,'created_job':ready['created_job'],'assigned':ready['assigned'],'root_member':ready['runtime_member'],
                        'limit_flags':ready['flags'],'handle_inheritable':ready['inheritable'],'memberships':ready['memberships'],
                        'protocol_result':ready['protocol_result'],'after_close':{'all_exited':exited,'observed_handles':len(handles)},
                        'recovery':{'recovery_required':recovered},'cleanup':parent_cleanup.get('cleanup'),'session_absent':parent_cleanup.get('session_absent'),
                        'preassign_descendants':ready['preassign_descendants'],'startup_processes':ready['startup_processes'],
                        'startup_exited_without_recovery':exited,'errors':ready['errors'],'runtime_exit':runtime_exit,'wrapper_force_killed':True,
                        'parent_session_cleanup':True,'owner_kill_process_handles_verified':alive_before_kill,
                        'wrapper_exit':run.returncode}
                    (folder/'result.json').write_text(json.dumps(result),encoding='utf-8')
                else:
                    try: out,err=run.communicate(timeout=140)
                    except subprocess.TimeoutExpired:
                        run.kill(); out,err=run.communicate(timeout=15)
                        raise RuntimeError('BRIDGE_TIMED_OUT_JOB_CLOSED_BY_OWNER_TERMINATION')
            (folder/'bridge-stdout.txt').write_text(out,encoding='utf-8'); (folder/'bridge-stderr.txt').write_text(err,encoding='utf-8')
            requests=[r for r in fixture.requests if r['case']==case]
            (folder/'provider-requests.json').write_text(json.dumps(requests,ensure_ascii=False,indent=2),encoding='utf-8')
            result=json.loads((folder/'result.json').read_text(encoding='utf-8-sig'))
            if case=='model-cancel':
                time.sleep(9); result['fixture_transport_aborted']=any(e['case']==case for e in fixture.transport)
                (folder/'result.json').write_text(json.dumps(result),encoding='utf-8')
            after=folder/'workspace-after.json'
            verdict=evaluate(case,result,requests,json.loads(after.read_text(encoding='utf-8-sig')) if after.exists() else {})
            (folder/'verdict.json').write_text(json.dumps(verdict,ensure_ascii=False,indent=2),encoding='utf-8')
            print(json.dumps(verdict,ensure_ascii=False),flush=True)
    return 0


if __name__=='__main__': raise SystemExit(main())

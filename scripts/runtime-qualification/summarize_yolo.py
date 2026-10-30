"""Publish allowlisted aggregate evidence only; raw host data remains ignored."""
from __future__ import annotations

import collections
import hashlib
import json
import pathlib

from qualify_yolo import ROOT, RUNS, evaluate
from yolo_fixture import CASES


def summarize() -> dict:
    pin = json.loads(pathlib.Path(__file__).with_name('yolo-runtime-identity.json').read_text())
    entries = []
    tools = set()
    for case in CASES:
        folder = RUNS/case
        if not (folder/'verdict.json').exists():
            entries.append({'case':case,'status':'NOT_RUN'}); continue
        verdict = json.loads((folder/'verdict.json').read_text(encoding='utf-8'))
        tools.update(verdict.get('advertised_tools',[]))
        raw = json.loads((folder/'result.json').read_text(encoding='utf-8-sig'))
        requests = json.loads((folder/'provider-requests.json').read_text(encoding='utf-8'))
        before_file, after_file = folder/'workspace-before.json', folder/'workspace-after.json'
        before = json.loads(before_file.read_text(encoding='utf-8-sig')) if before_file.exists() else {}
        after = json.loads(after_file.read_text(encoding='utf-8-sig')) if after_file.exists() else {}
        current = evaluate(case,raw,requests,before,after)
        # Transport abort/control-plane observations are stored by the live driver.
        verdict['status'] = current['status']
        if case == 'model-cancel' and current['status'] == 'STOP_ACKNOWLEDGED' and verdict.get('fixture_transport_aborted'):
            verdict['status'] = 'PASS'
        if verdict.get('control_plane_gets',0) and case in {'parent-dotenv','local-builtin-idle'}:
            verdict['status'] = 'FAIL'
        verdict['actual_permission_mode'] = current['actual_permission_mode']
        launch = json.loads((folder/'launch.json').read_text(encoding='utf-8-sig'))
        observation = raw.get('observation',{})
        files = json.loads((folder/'session-files.json').read_text(encoding='utf-8-sig')) if (folder/'session-files.json').exists() else {}
        categories = collections.Counter()
        for path in files:
            path = path.replace('\\','/')
            if path.startswith('workspace/'): category = 'workspace'
            elif '/plugins/' in path: category = 'plugins'
            elif '/bundled-skills/' in path: category = 'skills'
            elif '/runtime_tools/' in path: category = 'runtime_tools'
            elif '/db/' in path: category = 'database'
            elif '/rollout/' in path: category = 'model_io'
            elif path.startswith('runtime/log/'): category = 'logs'
            elif 'provider' in path or 'builtin' in path: category = 'provider'
            else: category = 'other'
            categories[category] += 1
        # No raw paths, PIDs, messages, hostnames, registry output or environment.
        safe = {key:verdict.get(key) for key in ('case','status','provider_requests','tool_results','powershell_ok',
            'powershell_rows','cleanup_complete','child_recovery_required','child_marker_count','runtime_exit',
            'forced_kill','observer_error_count','registry_absent_before_recovery','control_plane_gets','fixture_transport_aborted','actual_permission_mode')
            if key in verdict}
        safe.update({'session_file_categories':dict(sorted(categories.items())),
            'non_loopback_tcp_sample_count':sum(not value.startswith('127.') for value in observation.get('tcp_ipv4',[])),
            'outside_session_file_events':len(observation.get('file_events',[])),
            'watcher_dropped_events':observation.get('dropped_events'),
            'child_process_names':sorted({p['name'] for p in observation.get('observed_processes',[]) if p['name'] != 'candidate'}),
            'inherited_administrator_token':launch['administrator'],
            'registry_absent_final':raw['registry_absent_final'],
            'orphan_recovery_completed':(raw.get('children') or {}).get('recovered')})
        protocol = raw.get('protocol_result') or {}
        reverse = collections.Counter()
        for line in (folder/'stdout.txt').read_text(encoding='utf-8-sig').splitlines():
            try: frame = json.loads(line)
            except ValueError: continue
            if 'id' in frame and 'method' in frame: reverse[frame['method']] += 1
        safe['reverse_requests_rejected'] = dict(sorted(reverse.items()))
        if 'stop_ms' in protocol: safe['stop_ack_ms'] = protocol['stop_ms']
        if 'wrapper_token_cancelled' in protocol: safe['wrapper_token_cancelled'] = protocol['wrapper_token_cancelled']
        before_file, after_file = folder/'workspace-before.json', folder/'workspace-after.json'
        if before_file.exists() and after_file.exists():
            before = json.loads(before_file.read_text(encoding='utf-8-sig'))
            after = json.loads(after_file.read_text(encoding='utf-8-sig'))
            safe['workspace_delta'] = {'added':sorted(set(after)-set(before)), 'removed':sorted(set(before)-set(after)),
                'modified':sorted(p for p in set(before)&set(after) if before[p] != after[p])}
        entries.append(safe)
    profile_path = RUNS.parent/'full-campaign-profile.json'
    profile = json.loads(profile_path.read_text()) if profile_path.exists() else {}
    return {'schema_version':1,'phase':2,'status':'PARTIAL','qualification_only':True,
        'runtime_sha256':pin['runtime_sha256'],'builtin_sha256':pin['builtin_sha256'],
        'transport':'agent-server --stdio','session_mode':'yolo','tool_filters':False,
        'candidate_environment':'V0.1 redirected variables plus fixed Windows-only PATH/PATHEXT/ComSpec and paired builtin/personal files',
        'actual_provider_tool_surface':sorted(tools),'cases':entries,
        'real_zcode_metadata':{'unchanged':profile.get('unchanged'),
            'before_entries':len(profile.get('before',{}).get('entries',{})),
            'after_entries':len(profile.get('after',{}).get('entries',{})),
            'enumeration_errors':len(profile.get('before',{}).get('errors',[]))+len(profile.get('after',{}).get('errors',[]))},
        'real_api_used':False,'production_runtime_enabled':False,
        'ordinary_user_boundary':'NOT_PROVEN: inherited administrator token; de-elevation launch was denied, no security policy changed',
        'limitations':['Active development host, no PID-attributed whole-machine filesystem diff',
            '50ms IPv4 TCP/process sampling misses short connections/processes, IPv6, UDP, DNS and TLS names',
            'Canary PID plus exact birth time checks cover fixture-owned processes, not all possible descendants',
            'Actual plugin tool registration is observed; plugin/skill/browser/subagent execution not qualified here']}


def tool_schemas() -> dict:
    requests = json.loads((RUNS/'surface/provider-requests.json').read_text(encoding='utf-8'))
    tools = next(r['body']['tools'] for r in requests if r['body'].get('tools'))
    def structural(value):
        if isinstance(value,dict):
            return {k:structural(v) for k,v in value.items() if k not in {'description','title','examples'}}
        if isinstance(value,list): return [structural(v) for v in value]
        return value
    records = []
    for tool in tools:
        function = tool['function']
        parameters = function.get('parameters',{})
        records.append({'name':function['name'],'parameters':structural(parameters),
            'original_parameters_sha256':hashlib.sha256(json.dumps(parameters,sort_keys=True,separators=(',',':')).encode()).hexdigest()})
    return {'schema_version':1,'source':'actual fixed SEA YOLO Provider request; surface case',
        'projection':'parameter schema with description/title/examples omitted; original full request remains private',
        'tools':sorted(records,key=lambda t:t['name'])}


if __name__ == '__main__':
    destination = ROOT/'docs/runtime-qualification/yolo-evidence-summary.json'
    destination.write_text(json.dumps(summarize(),indent=2,ensure_ascii=False)+'\n',encoding='utf-8',newline='\n')
    destination.with_name('yolo-tool-schemas.json').write_text(json.dumps(tool_schemas(),indent=2,ensure_ascii=False)+'\n',encoding='utf-8',newline='\n')
    print(str(destination.relative_to(ROOT)))

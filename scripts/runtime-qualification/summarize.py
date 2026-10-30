"""Export an allowlisted, path-free summary. Never publish raw profile events or model logs."""
from __future__ import annotations

import collections
import hashlib
import ipaddress
import json
import pathlib
import re

from qualify import CASES, IDENTITY, ROOT, RUNS, evaluate


def export() -> dict:
    identity = json.loads(IDENTITY.read_text(encoding='utf-8'))
    cases = []
    for case in CASES:
        directory = RUNS / case
        result_file = directory/'result.json'
        if not result_file.is_file():
            cases.append({'case': case, 'status': 'NOT_RUN'})
            continue
        result = json.loads(result_file.read_text(encoding='utf-8-sig'))
        if result.get('runtime_sha256') != identity['runtime_sha256']:
            raise ValueError('MIXED_RUNTIME_EVIDENCE')
        requests = json.loads((directory/'provider-requests.json').read_text(encoding='utf-8'))
        stdout = (directory/'stdout.txt').read_text(encoding='utf-8-sig')
        verdict = evaluate(case, result, requests, stdout)
        # stop_result contains only this fixed protocol ACK, not arbitrary session state.
        if 'protocol_result' in verdict:
            verdict['protocol_result'].pop('stop_result', None)
        observation = result['observation']
        verdict['outside_file_events_by_root'] = dict(collections.Counter(e['root'] for e in observation['file_events']))
        verdict['outside_zcode_path_events'] = sum(bool(re.search(r'(^|[/\\])\.zcode([/\\]|$)', e['relative_path'], re.I))
                                                   for e in observation['file_events'])
        verdict['dropped_file_events'] = observation['dropped_events']
        verdict['observed_process_names'] = sorted({e['name'] for e in observation['observed_processes']})
        remote = set()
        loopback = set()
        for endpoint in observation['tcp_ipv4']:
            host, port = endpoint.rsplit(':', 1)
            (loopback if ipaddress.ip_address(host).is_loopback else remote).add(endpoint)
        verdict['nonloopback_tcp_ipv4'] = sorted(remote)
        verdict['loopback_tcp_connection_count'] = len(loopback)
        messages = [m for request in requests for m in request['body'].get('messages', []) if m.get('role') == 'tool']
        # Export only exact, known absent-tool results. Other payloads stay local.
        verdict['tool_denial_results'] = sorted({m['content'] for m in messages
            if isinstance(m.get('content'), str) and re.fullmatch(r'Tool not found: [A-Za-z0-9_-]+', m['content'])})
        files_file = directory/'session-files.json'
        if files_file.is_file():
            files = json.loads(files_file.read_text(encoding='utf-8-sig'))
            verdict['session_file_count_before_cleanup'] = len(files)
            verdict['session_file_roots'] = dict(collections.Counter(name.replace('\\','/').split('/')[0] for name in files))
        cases.append(verdict)
    summary_file = RUNS/'summary.json'
    campaign = json.loads(summary_file.read_text(encoding='utf-8')) if summary_file.exists() else {}
    return {'status': 'PARTIAL', 'runtime_identity': identity,
        'case_count': len(cases), 'verdict_counts': dict(collections.Counter(c['status'] for c in cases)),
        'cases': cases, 'real_zcode_metadata': campaign.get('real_zcode_metadata', {'status': 'NOT_PROVEN'}),
        'real_model_api_used': False, 'production_runtime_enabled': False,
        'limitations': ['Non-admin IPv4 TCP/process polling; short events, IPv6, UDP and DNS/TLS hostnames can be missed',
            'Global file events have no PID attribution; background writes prevent a complete clean-profile claim',
            'No full VM image/registry hive/network capture; no system security or audit policy changes',
            'Windows Services/Event Log/Processes/Network/Registry/Drivers/software APIs are absent from the observed builtin read tools',
            'Ancestor dotenv discovery remains a headless isolation blocker; server transport needs broader lifecycle/attack coverage']}


if __name__ == '__main__':
    result = export()
    target = ROOT/'docs/runtime-qualification/evidence-summary.json'
    target.parent.mkdir(exist_ok=True)
    target.write_text(json.dumps(result, indent=2, ensure_ascii=False) + '\n', encoding='utf-8', newline='\n')
    print(json.dumps({'exported': str(target.relative_to(ROOT)), 'case_count': result['case_count'],
                      'verdict_counts': result['verdict_counts'], 'status': result['status']}))

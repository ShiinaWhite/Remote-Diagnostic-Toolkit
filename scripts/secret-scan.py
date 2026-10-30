"""Scan public candidates and all Git blobs; emit rule/location only, never values.

Fixture exemptions are exact literals. Vendor copyright contacts are attribution,
not credentials. Reports, binaries and temporary sessions are never Git candidates.
"""
from __future__ import annotations
import collections
import json
import pathlib
import re
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
RULES = {
    'service_token': rb'(?:github_pat_[A-Za-z0-9_]{30,}|gh[pousr]_[A-Za-z0-9]{30,}|\bsk-(?:proj-)?[A-Za-z0-9_-]{24,}|\bAKIA[A-Z0-9]{16}\b|\bxox[baprs]-[A-Za-z0-9-]{20,})',
    'zai_key': rb'\b[0-9a-fA-F]{32}\.[0-9a-fA-F]{16,64}\b',
    'jwt': rb'\beyJ[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}',
    'private_key': rb'-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----\s*[A-Za-z0-9+/=\r\n]{64,}',
    'bearer': rb'(?i)\bBearer[ \t]+[A-Za-z0-9._~+/-]{16,}',
    'credential_url': rb'https?://[^\s/<>"\x00]{1,128}:[^\s/@<>"\x00]{1,128}@',
    'assignment': rb'''(?i)\b(?:api[_-]?key|(?:test|operator)?pin|password|passwd|access[_-]?token|refresh[_-]?token|authorization)\b["']?\s*[:=]\s*["']([^"'\r\n]{3,512})["']''',
}
FIXTURES = {b'94175826-test', b'rdt-only-test-key-invalid-for-any-service', b'api-key', b'G',
            b'rdt-qualification-invalid-local-fixture', b'Bearer fixture-wrong', b'Bearer '}


def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT)


def inspect(name, data):
    issues = []
    for rule, pattern in RULES.items():
        for match in re.finditer(pattern, data):
            value = match.group(1) if rule == 'assignment' else match.group(0)
            if value in FIXTURES or rule == 'assignment' and value.startswith((b'REPLACE_', b'example', b'https://example.invalid')):
                continue
            if rule == 'credential_url' and value == b'https://' + b'user:secret@' and data[match.end():].startswith(b'example.invalid'):
                continue
            issues.append({'file': name, 'rule': rule, 'line': data[:match.start()].count(b'\n') + 1})
    return issues


def main():
    issues = []
    names = set(filter(None, git('ls-files', '--cached', '--others', '--exclude-standard', '-z').decode().split('\0')))
    for name in sorted(names):
        path = ROOT / name
        if path.is_file():
            issues.extend(inspect(name, path.read_bytes()))
            if path.suffix.lower() in {'.rdt', '.exe', '.dll', '.pdb', '.log', '.trx'} or name.startswith(('Reports/', 'artifacts/')) and name != 'Reports/.gitkeep':
                issues.append({'file': name, 'rule': 'private_artifact_in_candidates', 'line': 0})
    blobs = 0
    for row in git('cat-file', '--batch-all-objects', '--batch-check=%(objectname) %(objecttype)').decode().splitlines():
        oid, kind = row.split()
        if kind in {'blob', 'commit', 'tag'}:
            issues.extend(inspect('git:' + oid, git('cat-file', kind, oid)))
            blobs += kind == 'blob'
    summary = {'status': 'FAIL' if issues else 'PASS', 'public_files': len(names), 'git_blobs': blobs,
               'findings': issues, 'rule_counts': dict(collections.Counter(i['rule'] for i in issues))}
    output = ROOT / 'artifacts/production-acceptance'
    output.mkdir(parents=True, exist_ok=True)
    (output / 'secret-scan.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    print(json.dumps(summary))
    return bool(issues)


if __name__ == '__main__':
    raise SystemExit(main())

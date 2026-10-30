"""Local production-adapter acceptance using pinned ZCode and a loopback-only fake G.

No real credentials or model API; no raw protocol/tool payloads are persisted.
This reuses the frozen fixture transport, not the old qualification adapter.
"""
from __future__ import annotations
import base64
import hashlib
import json
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scripts/runtime-qualification'))
from yolo_fixture import YoloFixture, quote

EXPECTED = '山西晋铁源-真实G端到端测试'


def scenario(case, launch):
    p = quote(str(pathlib.Path(launch['workspace']) / 'rdt-real-test.txt'))
    expected = quote(EXPECTED)
    prefix = ("[Console]::InputEncoding=[Text.UTF8Encoding]::new($false);"
              "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);"
              "$OutputEncoding=[Console]::OutputEncoding;$ErrorActionPreference='Stop';")
    scripts = [
        "[Environment]::OSVersion.VersionString;$PSVersionTable.PSVersion.ToString();'RDT_REAL_SYSTEM_OK'",
        f"[IO.File]::WriteAllText({p},{expected},[Text.UTF8Encoding]::new($false));if(!(Test-Path -LiteralPath {p})){{throw 'missing'}};'RDT_REAL_CREATED'",
        f"$s=[IO.File]::ReadAllText({p},[Text.UTF8Encoding]::new($false,$true));if($s -cne {expected}){{throw 'mismatch'}};'RDT_REAL_VERIFIED:'+$s",
        f"Remove-Item -LiteralPath {p};if(Test-Path -LiteralPath {p}){{throw 'remaining'}};'RDT_REAL_DELETED'",
    ]
    calls = []
    for i, script in enumerate(scripts):
        encoded = base64.b64encode((prefix + script).encode('utf-16le')).decode('ascii')
        calls.append(('Bash', {'command': '"' + launch['powershell'] + '" -NoLogo -NoProfile -NonInteractive -EncodedCommand ' + encoded, 'timeout': 20000}))
        if i == 1:
            calls.append(('Read', {'file_path': str(pathlib.Path(launch['workspace']) / 'rdt-real-test.txt')}))
    return calls


def main():
    pin = json.loads((ROOT / 'config/runtime-identity.json').read_text())
    for name, field in [('zcode-windows-x64.exe', 'sha256'), ('zcode-builtin.json', 'builtinSha256')]:
        if hashlib.sha256((ROOT / 'runtime' / name).read_bytes()).hexdigest() != pin[field]:
            raise RuntimeError('PINNED_IDENTITY_MISMATCH')
    build = subprocess.run(['dotnet', 'build', 'tools/Rdt.RuntimeAcceptance/Rdt.RuntimeAcceptance.csproj', '-c', 'Release', '--no-restore'], cwd=ROOT)
    if build.returncode:
        return build.returncode
    runs = ROOT / 'artifacts/production-acceptance'
    # The frozen transport maps model rdt-yolo-production to this launch file.
    (runs / 'production').mkdir(parents=True, exist_ok=True)
    with YoloFixture(runs, cases=['production'], scenario=scenario) as fixture:
        # The adapter's provider atomically publishes metadata before runtime startup.
        result = subprocess.run(['dotnet', str(ROOT / 'tools/Rdt.RuntimeAcceptance/bin/Release/net10.0/Rdt.RuntimeAcceptance.dll'), 'fixture', fixture.endpoint], cwd=ROOT, timeout=180)
        tools = [m.get('content', '') for r in fixture.requests for m in r['body'].get('messages', []) if m.get('role') == 'tool']
        summary = {'real_api_used': False, 'api_requests': len(fixture.requests), 'control_plane_gets': len(fixture.gets),
                   'tool_result_count': len(tools), 'unicode_in_provider_tool_results': any(EXPECTED in str(v) for v in tools),
                   'runtime_contract_received': any('[RDT runtime contract]' in str(m.get('content', '')) for r in fixture.requests for m in r['body'].get('messages', []) if m.get('role') == 'user'),
                   'exit_code': result.returncode}
        (runs / 'fixture/provider-evidence.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
        print(json.dumps(summary))
        return result.returncode


if __name__ == '__main__':
    raise SystemExit(main())

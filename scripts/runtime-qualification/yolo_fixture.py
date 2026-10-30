"""Fixed synthetic YOLO scenarios; loopback only, no forwarding or real credentials."""
from __future__ import annotations

import base64
import json
import pathlib
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from fixture_provider import FIXTURE_KEY

CASES = ("surface read write edit delete mkdir subprocess services processes eventlog os network scheduled "
         "registry-read registry-lifecycle privilege parent-dotenv local-builtin-idle glob grep patch "
         "tool-failure model-cancel tool-stop wrapper-cancel detached-child").split()


def quote(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def script(case: str, launch: dict) -> str:
    workspace = launch['workspace']
    reg = 'HKCU:\\' + launch['registry_path']
    scripts = {
        'services': "$data=@(Get-Service -ErrorAction Stop | Select-Object -First 5 Name,Status,DisplayName)",
        'processes': "$data=@(Get-Process -ErrorAction Stop | Select-Object -First 5 Id,ProcessName)",
        'eventlog': "$data=@(Get-WinEvent -LogName System -MaxEvents 1 -ErrorAction Stop | Select-Object Id,RecordId,ProviderName,LevelDisplayName,TimeCreated)",
        'os': "$data=@(Get-CimInstance Win32_OperatingSystem -ErrorAction Stop | Select-Object Caption,Version,BuildNumber,OSArchitecture)",
        'network': "$data=@(Get-NetTCPConnection -ErrorAction Stop | Select-Object -First 5 State,LocalPort,RemotePort,OwningProcess)",
        'scheduled': "$data=@(Get-ScheduledTask -ErrorAction Stop | Select-Object -First 5 TaskName,TaskPath,State)",
        'registry-read': "$data=@(Get-ItemProperty -LiteralPath " + quote(reg) + " -Name Fixture -ErrorAction Stop | Select-Object Fixture)",
        'parent-dotenv': "$data=@([ordered]@{canary=$env:RDT_DOTENV_CANARY;base_url=$env:ZCODE_BASE_URL;home=$env:HOME;userprofile=$env:USERPROFILE;localappdata=$env:LOCALAPPDATA})",
        'delete': "Remove-Item -LiteralPath " + quote(str(pathlib.Path(workspace)/'delete-target.txt')) + "; $data=@(@{absent=!(Test-Path -LiteralPath " + quote(str(pathlib.Path(workspace)/'delete-target.txt')) + ")})",
        'mkdir': "$p=" + quote(str(pathlib.Path(workspace)/'created-dir')) + "; New-Item -ItemType Directory -Path $p | Out-Null; $data=@(@{exists=(Test-Path -LiteralPath $p -PathType Container)})",
        'subprocess': "$c=Join-Path $env:SystemRoot 'System32\\cmd.exe'; $output=& $c /d /c echo RDT_CHILD_SENTINEL; $data=@(@{output=$output;exit=$LASTEXITCODE})",
        'tool-failure': "throw 'RDT_CONTROLLED_FAILURE'",
        'tool-stop': "Start-Sleep -Seconds 30; $data=@('UNEXPECTED_TOOL_COMPLETION')",
        'wrapper-cancel': "Start-Sleep -Seconds 30; $data=@('UNEXPECTED_TOOL_COMPLETION')",
        'privilege': "$a=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator); $old=$ErrorActionPreference; $ErrorActionPreference='Continue'; $out=& (Join-Path $env:SystemRoot 'System32\\fsutil.exe') dirty query $env:SystemDrive 2>&1; $code=$LASTEXITCODE; $ErrorActionPreference=$old; $data=@(@{administrator=$a;query_exit=$code;query_output=([string]($out -join '\n'))})",
    }
    if case == 'registry-lifecycle':
        scripts[case] = "$p=" + quote(reg) + """; if(Test-Path -LiteralPath $p){throw 'CANARY_COLLISION'};
New-Item -Path $p | Out-Null; New-ItemProperty -LiteralPath $p -Name Fixture -Value RDT_CREATED -PropertyType String | Out-Null;
$first=(Get-ItemProperty -LiteralPath $p -Name Fixture).Fixture;
Set-ItemProperty -LiteralPath $p -Name Fixture -Value RDT_MODIFIED;
$second=(Get-ItemProperty -LiteralPath $p -Name Fixture).Fixture;
Remove-Item -LiteralPath $p;
$data=@(@{created_read=$first;modified_read=$second;deleted=!(Test-Path -LiteralPath $p)})"""
    if case == 'detached-child':
        detached = marker(launch, 'detached.pid.json') + "; Start-Sleep -Seconds 75"
        encoded = base64.b64encode(detached.encode('utf-16le')).decode('ascii')
        scripts[case] = ("$child=Start-Process -FilePath " + quote(launch['powershell'])
            + " -WindowStyle Hidden -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-EncodedCommand','"
            + encoded + "') -PassThru; $until=(Get-Date).AddSeconds(10); while(!(Test-Path -LiteralPath "
            + quote(str(pathlib.Path(workspace)/'detached.pid.json')) + ") -and (Get-Date)-lt$until){Start-Sleep -Milliseconds 100}; $data=@(@{launched=$child.Id})")
    body = scripts[case]
    return ("$ErrorActionPreference='Stop'; " + marker(launch, 'active.pid.json') + "; try { " + body
        + "; $r=[ordered]@{case=" + quote(case) + ";ok=$true;count=@($data).Count;data=@($data)}; "
          "Write-Output ('RDT_YOLO_RESULT:' + ($r|ConvertTo-Json -Depth 6 -Compress)) "
          "} catch { $r=@{case=" + quote(case) + ";ok=$false;error=$_.Exception.Message}; "
          "Write-Output ('RDT_YOLO_RESULT:' + ($r|ConvertTo-Json -Compress)); exit 7 }")


def marker(launch: dict, filename: str) -> str:
    return ("[IO.File]::WriteAllText(" + quote(str(pathlib.Path(launch['workspace'])/filename))
            + ",(@{pid=$PID;start_ticks=(Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks;session_id="
            + quote(launch['session_id']) + "}|ConvertTo-Json -Compress))")


def calls(case: str, launch: dict) -> list[tuple[str, dict]]:
    root = pathlib.Path(launch['workspace'])
    if case in {'surface', 'local-builtin-idle', 'model-cancel'}:
        return []
    if case == 'read':
        return [('Read', {'file_path': str(root/'fixture.txt')})]
    if case == 'write':
        return [('Write', {'file_path': str(root/'written.txt'), 'content': 'RDT_WRITTEN_SENTINEL\n'}),
                ('Read', {'file_path': str(root/'written.txt')})]
    if case == 'edit':
        return [('Read', {'file_path': str(root/'edit-target.txt')}),
                ('Edit', {'file_path': str(root/'edit-target.txt'), 'old_string': 'RDT_BEFORE_EDIT', 'new_string': 'RDT_AFTER_EDIT'}),
                ('Read', {'file_path': str(root/'edit-target.txt')})]
    if case == 'glob':
        return [('Glob', {'pattern': '*.txt', 'path': str(root)})]
    if case == 'grep':
        return [('Grep', {'pattern': 'RDT_YOLO_SENTINEL', 'path': str(root), 'output_mode': 'content'})]
    if case == 'patch':
        return [('ApplyPatch', {'patch': '*** Begin Patch\n*** Add File: patch.txt\n+RDT_PATCH\n*** End Patch'})]
    encoded = base64.b64encode(script(case, launch).encode('utf-16le')).decode('ascii')
    return [('Bash', {'command': '"' + launch['powershell'] + '" -NoLogo -NoProfile -NonInteractive -EncodedCommand ' + encoded,
                     'timeout': 20000 if case not in {'tool-stop','wrapper-cancel'} else 60000})]


class YoloFixture:
    def __init__(self, runs: pathlib.Path):
        self.runs = runs
        self.requests: list[dict] = []
        self.gets: list[str] = []
        self.transport: list[dict] = []
        self.lock = threading.Lock()
        self.issued: dict[str, int] = {}
        provider = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_GET(self):
                if self.path.startswith('/status/'):
                    case = self.path.removeprefix('/status/')
                    with provider.lock:
                        count = sum(r['case'] == case for r in provider.requests)
                    response = json.dumps({'requests': count}).encode()
                    self.send_response(200)
                    self.send_header('Content-Length', str(len(response)))
                    self.end_headers()
                    self.wfile.write(response)
                else:
                    with provider.lock:
                        provider.gets.append(self.path)
                    # Record attempted control-plane access; do not invent a valid upstream schema.
                    self.send_error(503, 'Qualification control-plane canary')

            def do_POST(self):
                length = int(self.headers.get('Content-Length','0'))
                if self.path != '/v1/chat/completions' or not 0 < length < 4_000_000:
                    self.send_error(400); return
                if self.headers.get('Authorization') != 'Bearer ' + FIXTURE_KEY:
                    self.send_error(401); return
                body = json.loads(self.rfile.read(length))
                case = body.get('model','').removeprefix('rdt-yolo-')
                if case not in CASES:
                    self.send_error(400); return
                launch = json.loads((provider.runs/case/'launch.json').read_text(encoding='utf-8-sig'))
                with provider.lock:
                    provider.requests.append({'case':case,'path':self.path,'body':body})
                    sequence = calls(case, launch)
                    index = provider.issued.get(case,0)
                    call = sequence[index] if index < len(sequence) and body.get('tools') else None
                    if call:
                        provider.issued[case] = index + 1
                if case == 'model-cancel':
                    time.sleep(8)
                message = {'role':'assistant','content':'RDT_YOLO_DONE'}
                finish = 'stop'
                if call:
                    name, arguments = call
                    message = {'role':'assistant','content':None,'tool_calls':[{'id':f'call_yolo_{case}_{index}',
                        'type':'function','function':{'name':name,'arguments':json.dumps(arguments)}}]}
                    finish = 'tool_calls'
                envelope = {'id':'chatcmpl-yolo-fixture','created':int(time.time()),'model':body['model']}
                try:
                    self.send_response(200)
                    if body.get('stream'):
                        self.send_header('Content-Type','text/event-stream')
                        self.send_header('Connection','close'); self.end_headers()
                        if call:
                            message['tool_calls'][0]['index'] = 0
                        for delta, stop in ((message,None),({},finish)):
                            chunk = dict(envelope, object='chat.completion.chunk', choices=[{'index':0,'delta':delta,'finish_reason':stop}])
                            self.wfile.write(('data: '+json.dumps(chunk)+'\n\n').encode())
                        self.wfile.write(b'data: [DONE]\n\n'); self.wfile.flush(); self.close_connection = True
                    else:
                        response = json.dumps(dict(envelope,object='chat.completion',choices=[{'index':0,'message':message,'finish_reason':finish}],
                            usage={'prompt_tokens':1,'completion_tokens':1,'total_tokens':2})).encode()
                        self.send_header('Content-Type','application/json'); self.send_header('Content-Length',str(len(response)))
                        self.end_headers(); self.wfile.write(response)
                except ConnectionError as error:
                    with provider.lock:
                        provider.transport.append({'case':case,'error':type(error).__name__})

        self.server = ThreadingHTTPServer(('127.0.0.1',0),Handler)
        self.thread = threading.Thread(target=self.server.serve_forever,daemon=True)

    @property
    def endpoint(self):
        return f'http://127.0.0.1:{self.server.server_port}/v1'

    def __enter__(self):
        self.thread.start(); return self

    def __exit__(self,*_):
        self.server.shutdown(); self.server.server_close(); self.thread.join(timeout=5)

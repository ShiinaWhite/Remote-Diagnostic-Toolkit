"""Fixed lifecycle/Unicode fixtures. Every PowerShell command uses the same UTF-8 preamble."""
from __future__ import annotations
import base64
import pathlib

from yolo_fixture import calls as yolo_calls, marker, quote

CASES = ('normal detached-child detached-stop detached-wrapper-cancel session-stop model-cancel tool-cancel '
         'wrapper-cancel tool-failure runtime-error wrapper-cleanup wrapper-killed startup-race nested-job '
         'inject-create-failure inject-config-failure inject-assign-failure unicode-ps51 unicode-ps7 write edit').split()
STRINGS = ['山西晋铁源-中文诊断测试', '中文路径测试', '服务状态：正常']
UNICODE_PATH = '中文路径测试/中文诊断.txt'
PREAMBLE = """[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$ErrorActionPreference='Stop'
"""


def script(case: str, launch: dict, marker_name: str = 'active.pid.json') -> str:
    workspace = pathlib.Path(launch['workspace'])
    body = "$data=@(@{sentinel='RDT_NATIVE_OPERATIONAL';version=$PSVersionTable.PSVersion.ToString()})"
    if case in {'tool-cancel','wrapper-cancel','wrapper-cleanup'}:
        body = "Start-Sleep -Seconds 30; $data=@('UNEXPECTED_COMPLETION')"
    if case == 'tool-failure':
        body = "throw 'RDT_CONTROLLED_FAILURE'"
    if case in {'detached-child','detached-stop','detached-wrapper-cancel','wrapper-killed','runtime-error'}:
        detached = PREAMBLE + marker(launch,'detached.pid.json') + '; Start-Sleep -Seconds 75'
        encoded = base64.b64encode(detached.encode('utf-16le')).decode()
        body = ("$child=Start-Process -FilePath " + quote(launch['powershell'])
            + " -WindowStyle Hidden -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-EncodedCommand','"
            + encoded + "') -PassThru; $until=(Get-Date).AddSeconds(10); while(!(Test-Path -LiteralPath "
            + quote(str(workspace/'detached.pid.json')) + ") -and (Get-Date)-lt$until){Start-Sleep -Milliseconds 50}; "
            + "if(!(Test-Path -LiteralPath " + quote(str(workspace/'detached.pid.json')) + ")){throw 'DETACHED_NOT_STARTED'}; "
            + "Start-Sleep -Milliseconds 600; $data=@(@{launched=$true})")
        if case == 'runtime-error':
            body += "; Start-Sleep -Seconds 30" # Wrapper injects a root-only runtime failure after API membership proof.
    if case.startswith('unicode-'):
        literals = ','.join(quote(value) for value in STRINGS)
        file = quote(str(workspace/pathlib.Path(UNICODE_PATH)))
        directory = quote(str(workspace/'中文路径测试'))
        body = ("$strings=@(" + literals + "); foreach($s in $strings){[Console]::WriteLine('RDT_UNICODE_DIRECT:'+$s)}; "
            + "$json=([ordered]@{strings=$strings}|ConvertTo-Json -Depth 5 -Compress); [Console]::WriteLine('RDT_UNICODE_JSON:'+$json); "
            + "New-Item -ItemType Directory -Path " + directory + " | Out-Null; $p=" + file + "; "
            + "[IO.File]::WriteAllText($p,($strings -join \"`n\"),[Text.UTF8Encoding]::new($false)); "
            + "$back=[IO.File]::ReadAllText($p,[Text.UTF8Encoding]::new($false,$true)); "
            + "$data=@([ordered]@{strings=$strings;json=($json|ConvertFrom-Json);file=$back;"
            + "relative_path='中文路径测试/中文诊断.txt';leaf=(Get-Item -LiteralPath $p).Name;version=$PSVersionTable.PSVersion.ToString()})")
    return (PREAMBLE + marker(launch,marker_name) + "; Start-Sleep -Milliseconds 600; try { " + body
        + "; $r=[ordered]@{case=" + quote(case) + ";ok=$true;data=@($data)}; Write-Output ('RDT_YOLO_RESULT:'+($r|ConvertTo-Json -Depth 8 -Compress)) "
          "} catch { Write-Output ('RDT_YOLO_RESULT:'+(@{case=" + quote(case)
        + ";ok=$false;error=$_.Exception.Message}|ConvertTo-Json -Compress)); exit 7 }")


def calls(case: str, launch: dict) -> list[tuple[str,dict]]:
    if case in {'model-cancel','startup-race'} or case.startswith('inject-'): return []
    if case in {'write','edit'}: return yolo_calls(case,launch)
    executable = launch['powershell7'] if case == 'unicode-ps7' else launch['powershell']
    encoded = base64.b64encode(script(case,launch).encode('utf-16le')).decode()
    command='"'+executable+'" -NoLogo -NoProfile -NonInteractive -EncodedCommand '+encoded
    if len(command)>7800: raise ValueError('SYNTHETIC_COMMAND_EXCEEDS_WINDOWS_CMD_BUDGET')
    result = [('Bash',{'command':command,'timeout':60000})]
    if case=='detached-wrapper-cancel':
        # First parent tool completes, then another tool holds the turn open. The
        # detached orphan belongs to the first tool, not the second tool's tree.
        hold=base64.b64encode(script('wrapper-cancel',launch,'hold.pid.json').encode('utf-16le')).decode()
        result.append(('Bash',{'command':'"'+executable+'" -NoLogo -NoProfile -NonInteractive -EncodedCommand '+hold,'timeout':60000}))
    if case.startswith('unicode-'):
        result.append(('Read',{'file_path':str(pathlib.Path(launch['workspace'])/pathlib.Path(UNICODE_PATH))}))
    return result

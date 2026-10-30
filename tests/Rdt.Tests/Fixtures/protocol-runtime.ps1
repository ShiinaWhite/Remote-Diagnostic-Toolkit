param([string]$Scenario = 'success')
$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$sent = $false
$stopped = $false
$model = 'fixture-model'
if ($Scenario -eq 'early-exit') { exit 27 }
if ($Scenario -eq 'malformed') { [Console]::WriteLine('{broken'); exit 28 }
if ($Scenario -eq 'stderr-invalid') { $raw=[Console]::OpenStandardError(); $raw.WriteByte(255); $raw.Flush() }
if ($Scenario -eq 'stdout-invalid') { $raw=[Console]::OpenStandardOutput(); $raw.WriteByte(255); $raw.WriteByte(254); $raw.Flush(); exit 28 }
while ($null -ne ($line = [Console]::ReadLine())) {
    $f = $line | ConvertFrom-Json
    if (-not $f.method) { continue }
    $method = $f.method
    if ($method -eq 'session/create') {
        [IO.File]::WriteAllText((Join-Path $env:TEMP 'create-seen'), 'created')
        $sessionId = $f.params.sessionId
        if ($f.params.mode -ne 'yolo') { exit 29 }
        if ($Scenario -eq 'create-failure') {
            [Console]::WriteLine((@{id=$f.id;error=@{code=-1;message='fixture-secret-must-not-persist'}} | ConvertTo-Json -Compress -Depth 30))
            continue
        }
    }
    if ($method -eq 'session/send') {
        $sent = $true
        if ($Scenario -eq 'send-failure') {
            [Console]::WriteLine((@{id=$f.id;error=@{code=-2;message='fixture-secret-must-not-persist'}} | ConvertTo-Json -Compress -Depth 30))
            continue
        }
        if ($Scenario -eq 'detached') {
            $ps = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
            $child = Start-Process -FilePath $ps -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60' -WindowStyle Hidden -PassThru
            [IO.File]::WriteAllText((Join-Path $env:TEMP 'child-pid'), [string]$child.Id)
        }
    }
    if ($method -eq 'session/stop') {
        $stopped = $true
        [IO.File]::WriteAllText((Join-Path $env:TEMP 'stop-seen'), 'stopped')
    }
    $messages = @()
    $projection = @{status='idle';activeToolCalls=@();pendingPermissions=@()}
    if ($sent -and -not $stopped) {
        if ($Scenario -in @('hang','detached')) { $projection.status='busy' }
        elseif ($Scenario -eq 'auth-error') { $projection.lastError=@{code='401';message='Unauthorized fixture-secret-must-not-persist'} }
        elseif ($Scenario -eq 'network-error') { $projection.lastError=@{code='ECONNREFUSED';message='network fixture-secret-must-not-persist'} }
        else {
            # ASCII source; manufacture Unicode independently of PS 5.1 source decoding.
            $unicode = -join ([char[]]@(0x5C71,0x897F,0x664B,0x94C1,0x6E90))
            $messages = @(@{info=@{role='assistant';finish='stop';time=@{completed=1}};parts=@(
                @{type='tool';tool='Bash';callId='fixture-call'},
                @{type='text';text=($unicode + ' fixture-secret-must-not-persist')}
            )})
        }
    }
    $state = @{settings=@{mode=@{current='yolo'};permission=@{mode='yolo'};model=@{current=@{providerId='rdt-g';modelId=$model}}};projection=$projection;messages=$messages}
    if ($method -eq 'session/send') { $result = @{accepted=$true} }
    elseif ($method -eq 'session/stop') { $result = @{stopped=$true} }
    else { $result = $state }
    [Console]::WriteLine((@{id=$f.id;result=$result} | ConvertTo-Json -Compress -Depth 30))
}
if ($Scenario -eq 'ignore-eof') { Start-Sleep -Seconds 60 }

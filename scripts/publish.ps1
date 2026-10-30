param([ValidateSet('win-x64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    $identity = Get-Content -LiteralPath 'config/runtime-identity.json' -Raw | ConvertFrom-Json
    $binary = Join-Path 'runtime' $identity.filename
    $builtin = Join-Path 'runtime' $identity.builtinFilename
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw 'RUNTIME_MISSING' }
    if ((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne $identity.sha256) { throw 'RUNTIME_HASH_MISMATCH' }
    if (-not (Test-Path -LiteralPath $builtin -PathType Leaf)) { throw 'BUILTIN_MISSING' }
    if ((Get-FileHash -LiteralPath $builtin -Algorithm SHA256).Hash -ne $identity.builtinSha256) { throw 'BUILTIN_HASH_MISMATCH' }
    dotnet publish src/Rdt.Cli/Rdt.Cli.csproj -c Release -r $Runtime --self-contained true -o "artifacts/$Runtime"
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item -LiteralPath $binary -Destination "artifacts/$Runtime/runtime/zcode-windows-x64.exe"
    if ((Get-FileHash -LiteralPath "artifacts/$Runtime/runtime/zcode-windows-x64.exe" -Algorithm SHA256).Hash -ne $identity.sha256) { throw 'PACKAGED_RUNTIME_HASH_MISMATCH' }
    if ((Get-FileHash -LiteralPath "artifacts/$Runtime/runtime/zcode-builtin.json" -Algorithm SHA256).Hash -ne $identity.builtinSha256) { throw 'PACKAGED_BUILTIN_HASH_MISMATCH' }
} finally { Pop-Location }

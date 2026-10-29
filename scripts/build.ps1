$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    dotnet restore Rdt.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet build Rdt.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
} finally { Pop-Location }

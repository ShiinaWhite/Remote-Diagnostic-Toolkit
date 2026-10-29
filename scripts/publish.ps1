param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    dotnet publish src/Rdt.Cli/Rdt.Cli.csproj -c Release -r $Runtime --self-contained true -o "artifacts/$Runtime"
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    # Upstream runtime is deliberately not downloaded or bundled.
} finally { Pop-Location }

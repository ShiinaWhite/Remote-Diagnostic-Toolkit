$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    dotnet test Rdt.slnx -c Release --logger 'trx;LogFileName=rdt-tests.trx' --results-directory TestResults
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
} finally { Pop-Location }

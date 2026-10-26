$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
$output = Join-Path $modRoot 'artifacts/tests'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:exe /langversion:5 "/out:$output/TransportTests.exe" (Join-Path $modRoot 'src/Transport.cs') (Join-Path $modRoot 'src/CoopState.cs') (Join-Path $modRoot 'tests/TransportTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
& "$output/TransportTests.exe" "$output/results.json"
if ($LASTEXITCODE -ne 0) { throw 'Transport tests failed.' }
# Mutation twin: with keepalives off, the idle-link check must fail (a stalled game loop used to drop the link).
$env:BT_MUTANT = 'no-keepalive'
try { $ErrorActionPreference = 'Continue'; $mutant = & "$output/TransportTests.exe" 2>&1 | Out-String; $mutantExit = $LASTEXITCODE }
finally { $ErrorActionPreference = 'Stop'; $env:BT_MUTANT = $null }
if ($mutantExit -eq 0 -or $mutant -notmatch 'idle link survives') { throw 'Mutation twin no-keepalive was not caught.' }
'Mutation twin no-keepalive caught.'

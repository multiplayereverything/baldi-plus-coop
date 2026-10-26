param([Parameter(ValueFromRemainingArguments=$true)][string[]]$WatchArguments)
$modRoot = Split-Path $PSScriptRoot -Parent
$env:WATCHSKILL_DATA_DIR = Join-Path $modRoot 'private/watch-data'
& (Join-Path $modRoot '.venv/Scripts/watch-skill.exe') @WatchArguments
exit $LASTEXITCODE

[CmdletBinding()]
param([Parameter(Mandatory)][string]$SoakRun)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
. (Join-Path $repo 'tools/dev-common.ps1')
$run = Get-CheckedPath (Join-Path $repo 'artifacts/qa') (Resolve-Path -LiteralPath $SoakRun).Path
$save = Join-Path $run 'host/Klei/OxygenNotIncluded/save_files/Fixture/Fixture.sav'
$game = Join-Path $run 'GameCopy/OxygenNotIncluded_Data/Managed/Assembly-CSharp.dll'
$dst = Join-Path $run 'soak-input.json'
if (!(Select-String -LiteralPath (Join-Path $run 'client.report') -Pattern '^SOAK three concurrent guests Live' -Quiet)) {
    throw 'Wait for the three connected clients before capturing the source identity.'
}
$data = [ordered]@{ saveSha256=(Get-FileHash -LiteralPath $save).Hash; saveBytes=(Get-Item -LiteralPath $save).Length;
    gameSha256=(Get-FileHash -LiteralPath $game).Hash; speedIndex=0 }
if (Test-Path -LiteralPath $dst) {
    $prior = Get-Content -LiteralPath $dst -Raw | ConvertFrom-Json
    if ($prior.saveSha256 -ne $data.saveSha256 -or $prior.gameSha256 -ne $data.gameSha256) {
        throw 'An existing input manifest describes different source files.'
    }
} else { [IO.File]::WriteAllText($dst, ($data | ConvertTo-Json)) }
Write-Output $dst

[CmdletBinding()]
param([Parameter(Mandatory)][string]$SoakRun,[string]$SavePath,[string]$GameDir=$env:ONI_GAME_DIR)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
. (Join-Path $repo 'tools/dev-common.ps1')
$soak = Get-CheckedPath (Join-Path $repo 'artifacts/qa') (Resolve-Path -LiteralPath $SoakRun).Path
$expected = Get-Content -LiteralPath (Join-Path $soak 'soak-input.json') -Raw | ConvertFrom-Json
if (!(Select-String -LiteralPath (Join-Path $soak 'client.report') -Pattern '^PASS real host, three TCP guests' -Quiet)) {
    throw 'Wait until the connected soak has finished and released its ONI process.'
}
if (!$SavePath) {
    $docs = [Environment]::GetFolderPath('MyDocuments')
    $root = Join-Path $docs 'Klei/OxygenNotIncluded'
    $source = Get-ChildItem -LiteralPath $root -Filter '*.sav' -Recurse -File |
        Where-Object { $_.Length -eq $expected.saveBytes -and (Get-FileHash -LiteralPath $_.FullName).Hash -eq $expected.saveSha256 } |
        Select-Object -First 1
    if (!$source) { throw 'Set -SavePath to a local copy of the exact same initial .sav used by the connected run.' }
    $SavePath = $source.FullName
}
if ((Get-FileHash -LiteralPath $SavePath).Hash -ne $expected.saveSha256) { throw 'Single-player and connected saves differ.' }
$old = $env:QY_PERF_SOAK_RUN
try {
    $env:QY_PERF_SOAK_RUN = $soak
    & (Join-Path $PSScriptRoot '../Game/run.ps1') -SoloPerformance -SavePath $SavePath -GameDir $GameDir -TimeoutSeconds 850
} finally { [Environment]::SetEnvironmentVariable('QY_PERF_SOAK_RUN',$old) }

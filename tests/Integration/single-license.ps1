# Run sequentially. The record/replay pair deliberately uses separate Unity processes.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EarlySave,
    [Parameter(Mandatory)][string]$MatureSave,
    [Parameter(Mandatory)][string]$GameDir
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$gameRunner = Join-Path $PSScriptRoot 'Game/run.ps1'
$EarlySave = (Resolve-Path -LiteralPath $EarlySave).Path
$MatureSave = (Resolve-Path -LiteralPath $MatureSave).Path
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path

function Invoke-GamePhase([hashtable]$Options) {
    $directory = $null
    & $gameRunner -GameDir $GameDir @Options | ForEach-Object {
        if ($_ -is [string] -and $_.StartsWith('Run directory: ')) {
            $directory = $_.Substring('Run directory: '.Length)
        }
        $_ | Out-Host
    }
    if (!$directory -or !(Test-Path -LiteralPath $directory -PathType Container)) {
        throw 'The game runner did not report its evidence directory.'
    }
    return $directory
}

dotnet test (Join-Path $repo 'tests/Core.Tests/Core.Tests.csproj') -c Release
if ($LASTEXITCODE) { throw 'Core tests failed.' }
$record = Invoke-GamePhase @{ NativeRecord=$true; Persistence=$true; SavePath=$EarlySave; TimeoutSeconds=260 }
$recording = Join-Path $record 'native-recording'
$replay = Invoke-GamePhase @{ NativeReplay=$recording; TimeoutSeconds=260 }
$materials = Invoke-GamePhase @{ MaterialCompetition=$true; SavePath=$MatureSave; TimeoutSeconds=350 }
$terrain = Invoke-GamePhase @{ TerrainNetworks=$true; SavePath=$EarlySave }
$lifecycle = Invoke-GamePhase @{ NativeLifecycle=$true; NativeReplay=$recording; SavePath=$EarlySave }
$steam = Invoke-GamePhase @{ SteamLobby=$true; SavePath=$EarlySave }
$soak = Invoke-GamePhase @{ SoakSeconds=3600; SavePath=$MatureSave }
& (Join-Path $PSScriptRoot 'Performance/run.ps1') -SoakRun $soak -SavePath $MatureSave -GameDir $GameDir
& (Join-Path $PSScriptRoot 'Packaging/smoke.ps1')

[pscustomobject]@{ Recording=$record; Replay=$replay; MaterialCompetition=$materials; TerrainNetworks=$terrain;
    NativeLifecycle=$lifecycle; SteamLobby=$steam; Soak=$soak }

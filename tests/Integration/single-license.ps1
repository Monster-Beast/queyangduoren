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

function Assert-Report([string]$Directory, [string]$Name, [string[]]$Markers) {
    $report = Join-Path $Directory $Name
    foreach ($marker in $Markers) {
        if (!(Select-String -LiteralPath $report -Pattern $marker -Quiet)) {
            throw "Missing $marker in $report"
        }
    }
    if (Select-String -LiteralPath $report -Pattern '^FAIL |^FEATURE (FAIL|SKIP) |^NATIVE_.*SKIPPED' -Quiet) {
        throw "A required scenario was skipped or failed in $report"
    }
}

dotnet test (Join-Path $repo 'tests/Core.Tests/Core.Tests.csproj') -c Release
if ($LASTEXITCODE) { throw 'Core tests failed.' }
$transportProject = Join-Path $PSScriptRoot 'Transport/TransportTests.csproj'
dotnet build $transportProject -c Release
if ($LASTEXITCODE) { throw 'TCP fixture build failed.' }
$transport = Join-Path $PSScriptRoot 'Transport/bin/Release/net48/TransportTests.exe'
& $transport
if ($LASTEXITCODE) { throw 'TCP transport check failed.' }
foreach ($transfer in '--late-join','--native-save') {
    & $transport $transfer
    if ($LASTEXITCODE) { throw "Transfer check failed: $transfer" }
}
$record = Invoke-GamePhase @{ NativeRecord=$true; Persistence=$true; SavePath=$EarlySave; TimeoutSeconds=260 }
Assert-Report $record 'host.report' @('^NATIVE_OVERLAP_HOST','^NATIVE_MOP_HOST','^NATIVE_PRINTED_HOST',
    '^NATIVE_ECOLOGY_HOST','^NATIVE_FOOD_CHAIN_HOST','^NATIVE_EQUIPMENT_HOST .*visibleBuild=',
    '^NATIVE_PORTS_HOST','^NATIVE_FILTER_HOST','^NATIVE_RECIPE_HOST','^NATIVE_DOOR_HOST',
    '^NATIVE_PRINTED_DEATH_HOST','^FEATURE_CHECKS_DONE')
Assert-Report $record 'readback.report' @('^PASS native host save re-opened')
$recording = Join-Path $record 'native-recording'
$replay = Invoke-GamePhase @{ NativeReplay=$recording; TimeoutSeconds=260 }
Assert-Report $replay 'guest-native.report' @('^NATIVE_STOCK_TOOLS_GUEST','^NATIVE_PORTS_GUEST',
    '^NATIVE_GUEST_UTILITIES','^NATIVE_EQUIPMENT_GUEST .*symbolReadback=True','^NATIVE_STALE_SETTING',
    '^NATIVE_PRINTED_GUEST','^NATIVE_PRINTED_DEATH_GUEST','^NATIVE_BUILDING_DETAILS',
    '^NATIVE_COUNTERS simStep=0 findChore=0 setChore=0 saveAttempts=0 worldStarts=0 ')
$materials = Invoke-GamePhase @{ MaterialCompetition=$true; SavePath=$MatureSave; TimeoutSeconds=350 }
$terrain = Invoke-GamePhase @{ TerrainNetworks=$true; SavePath=$EarlySave }
$lifecycle = Invoke-GamePhase @{ NativeLifecycle=$true; NativeReplay=$recording; SavePath=$EarlySave }
$steam = Invoke-GamePhase @{ SteamLobby=$true; SavePath=$EarlySave }
$soak = Invoke-GamePhase @{ SoakSeconds=3600; SavePath=$MatureSave }
$expectedPackage = (Get-Content -LiteralPath (Join-Path $record 'host-input.json') -Raw | ConvertFrom-Json).package
foreach ($directory in @($replay,$materials,$terrain,$lifecycle,$steam,$soak)) {
    $manifest = Get-ChildItem -LiteralPath $directory -File -Filter '*-input.json' |
        Where-Object { $_.Name -ne 'soak-input.json' } | Select-Object -First 1
    if (!$manifest -or (Get-Content -LiteralPath $manifest.FullName -Raw | ConvertFrom-Json).package -ne $expectedPackage) {
        throw "Game phases loaded different packages. Check $directory"
    }
}
& (Join-Path $PSScriptRoot 'Performance/run.ps1') -SoakRun $soak -SavePath $MatureSave -GameDir $GameDir
& (Join-Path $PSScriptRoot 'Packaging/smoke.ps1')

[pscustomobject]@{ Recording=$record; Replay=$replay; MaterialCompetition=$materials; TerrainNetworks=$terrain;
    NativeLifecycle=$lifecycle; SteamLobby=$steam; Soak=$soak }

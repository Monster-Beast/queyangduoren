[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunRoot,
    [Parameter(Mandatory)][string]$GameCopy,
    [int]$TimeoutSeconds = 160
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
. (Join-Path $repo 'tools/dev-common.ps1')
$qa = (Resolve-Path (Join-Path $repo 'artifacts/qa')).Path
$run = Get-CheckedPath $qa (Resolve-Path -LiteralPath $RunRoot).Path
$copy = Get-CheckedPath $run (Resolve-Path -LiteralPath $GameCopy).Path
$scratch = Get-CheckedPath $run (Join-Path $run 'readback')
if (Test-Path -LiteralPath $scratch) { throw 'Readback profile must start empty.' }
$fixture = Get-CheckedPath $run (Join-Path $run 'host/Klei/OxygenNotIncluded/save_files/QyPersistence')
$original = Join-Path $fixture 'QyPersistence.sav'
$before = (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash
$project = Join-Path $PSScriptRoot '../Persistence/Readback.csproj'
$envKeys = @('QY_SMOKE_ROOT','QY_SMOKE_ROLE','QY_SMOKE_REPORT','QY_SMOKE_SAVE','ONI_GAME_DIR')
$savedEnv = @{}
foreach ($key in $envKeys) { $savedEnv[$key] = [Environment]::GetEnvironmentVariable($key) }
$proc = $null
try {
    $env:ONI_GAME_DIR = $copy
    dotnet build $project -c Release
    if ($LASTEXITCODE) { throw 'Single-player readback driver build failed.' }
    $klei = Join-Path $scratch 'Klei'
    $profile = Join-Path $klei 'OxygenNotIncluded'
    $mods = Join-Path $profile 'mods'
    $mod = Join-Path $mods 'dev/NativeReadback'
    [IO.Directory]::CreateDirectory($mod) | Out-Null
    $id = 'Queyang.QA.NativeReadback'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../Persistence/bin/Release/net48/Readback.dll') -Destination $mod
    [IO.File]::WriteAllLines((Join-Path $mod 'mod.yaml'), @('title: Native readback QA', 'description: Isolated single-player readback driver', "staticID: $id"))
    Copy-Item -LiteralPath (Join-Path $repo 'src/Game/metadata/mod_info.yaml') -Destination $mod
    $db = @{version=1;mods=@(@{label=@{distribution_platform=4;id='NativeReadback';title='Native readback QA';version=0};status=1;enabled=$true;enabledForDlc=@('');crash_count=0;reinstall_path=$null;staticID=$id})}
    [IO.File]::WriteAllText((Join-Path $mods 'mods.json'), ($db | ConvertTo-Json -Depth 6))
    Copy-Item -LiteralPath (Join-Path $run 'host/Klei/OxygenNotIncluded/kplayerprefs.yaml') -Destination $profile
    $dir = Join-Path $profile 'save_files/QyPersistence'
    [IO.Directory]::CreateDirectory($dir) | Out-Null
    $env:QY_SMOKE_SAVE = Join-Path $dir 'QyPersistence.sav'
    Copy-Item -LiteralPath $original -Destination $env:QY_SMOKE_SAVE
    Copy-Item -LiteralPath (Join-Path $fixture 'marker.txt') -Destination $dir
    $env:QY_SMOKE_ROOT = $klei
    $env:QY_SMOKE_ROLE = 'readback'
    $env:QY_SMOKE_REPORT = Join-Path $run 'readback.report'
    $log = Join-Path $run 'readback.log'
    $proc = Start-Process -FilePath (Join-Path $copy 'OxygenNotIncluded.exe') -WorkingDirectory $copy -ArgumentList @('-batchmode','-screen-fullscreen','0','-logFile',('"' + $log + '"')) -WindowStyle Hidden -PassThru
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (Test-Path -LiteralPath $env:QY_SMOKE_REPORT) {
            $lines = Get-Content -LiteralPath $env:QY_SMOKE_REPORT
            if ($lines -match '^FAIL ') { throw "Single-player readback failed; see $log" }
            if ($lines -match '^PASS ') {
                if ($before -ne (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash) { throw 'Source fixture changed.' }
                $lines
                return
            }
        }
        if ($proc.HasExited) { throw "Readback exited early; see $log" }
        Start-Sleep -Milliseconds 400
    } while ($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw "Readback timed out; see $log"
} finally {
    if ($proc -and !$proc.HasExited) { Stop-Process -Id $proc.Id -Force; $proc.WaitForExit() }
    foreach ($key in $envKeys) { [Environment]::SetEnvironmentVariable($key, $savedEnv[$key]) }
    if (Test-Path -LiteralPath $scratch) {
        $owned = Get-CheckedPath $run (Resolve-Path -LiteralPath $scratch).Path
        Remove-Item -LiteralPath $owned -Recurse -Force
    }
}

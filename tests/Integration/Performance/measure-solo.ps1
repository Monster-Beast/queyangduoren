[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunRoot,[Parameter(Mandatory)][string]$GameCopy,
      [Parameter(Mandatory)][string]$SavePath,[int]$TimeoutSeconds=850)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
. (Join-Path $repo 'tools/dev-common.ps1')
$qa = (Resolve-Path (Join-Path $repo 'artifacts/qa')).Path
$run = Get-CheckedPath $qa (Resolve-Path -LiteralPath $RunRoot).Path
$copy=Get-CheckedPath $run (Resolve-Path -LiteralPath $GameCopy).Path
$soak=Get-CheckedPath $qa (Resolve-Path -LiteralPath $env:QY_PERF_SOAK_RUN).Path
$expected=Get-Content -LiteralPath (Join-Path $soak 'soak-input.json') -Raw|ConvertFrom-Json
if ((Get-FileHash -LiteralPath (Join-Path $copy 'OxygenNotIncluded_Data/Managed/Assembly-CSharp.dll')).Hash -ne $expected.gameSha256 -or
    (Get-FileHash -LiteralPath $SavePath).Hash -ne $expected.saveSha256) { throw 'Build or input save differs from connected run.' }
$scratch=Get-CheckedPath $run (Join-Path $run 'solo-profile')
if (Test-Path -LiteralPath $scratch) { throw 'Solo profile already exists.' }
$proc=$null
try {
    dotnet build (Join-Path $PSScriptRoot 'SoloPerf.csproj') -c Release "-p:ONI_GAME_DIR=$copy"
    if ($LASTEXITCODE) { throw 'Standalone performance fixture failed to build.' }
    $klei=Join-Path $scratch 'Klei'
    $profile=Join-Path $klei 'OxygenNotIncluded'
    $mods=Join-Path $profile 'mods'
    $mod=Join-Path $mods 'dev/SoloPerf'
    [IO.Directory]::CreateDirectory($mod)|Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net48/SoloPerf.dll') -Destination $mod
    $id='Queyang.QA.SoloPerf'
    [IO.File]::WriteAllLines((Join-Path $mod 'mod.yaml'),@('title: Solo performance QA','description: Isolated baseline fixture',"staticID: $id"))
    Copy-Item -LiteralPath (Join-Path $repo 'src/Game/metadata/mod_info.yaml') -Destination $mod
    $db=@{version=1;mods=@(@{label=@{distribution_platform=4;id='SoloPerf';title='Solo performance QA';version=0};status=1;enabled=$true;enabledForDlc=@('');crash_count=0;reinstall_path=$null;staticID=$id})}
    [IO.File]::WriteAllText((Join-Path $mods 'mods.json'),($db|ConvertTo-Json -Depth 6))
    $prefs=@('strings:', '  SavesDefaultToCloud: Disabled','ints:','  EXPANSION1_ID.ENABLED: 0','  DLC2_ID.ENABLED: 0','  DLC3_ID.ENABLED: 0','  DLC4_ID.ENABLED: 0','  DLC5_ID.ENABLED: 0','  COSMETIC1_ID.ENABLED: 0','  LanguageConfirmationVersion: 2','floats: {}')
    [IO.File]::WriteAllLines((Join-Path $profile 'kplayerprefs.yaml'),$prefs)
    $dir=Join-Path $profile 'save_files/Fixture'
    [IO.Directory]::CreateDirectory($dir)|Out-Null
    $env:QY_SMOKE_ROOT=$klei
    $env:QY_SMOKE_ROLE='solo-performance'
    $env:QY_SMOKE_SAVE=Join-Path $dir 'Fixture.sav'
    $env:QY_SMOKE_REPORT=Join-Path $run 'solo.report'
    $env:SteamAppId='457140'
    $env:SteamGameId='457140'
    Copy-Item -LiteralPath $SavePath -Destination $env:QY_SMOKE_SAVE
    $log=Join-Path $run 'solo.log'
    $proc=Start-Process -FilePath (Join-Path $copy 'OxygenNotIncluded.exe') -WorkingDirectory $copy -ArgumentList @('-batchmode','-screen-fullscreen','0','-logFile',('"' + $log + '"')) -WindowStyle Hidden -PassThru
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (Test-Path -LiteralPath $env:QY_SMOKE_REPORT) {
            $lines=Get-Content -LiteralPath $env:QY_SMOKE_REPORT
            if ($lines -match '^FAIL ') { throw "Single-player measurement failed; see $log" }
            if ($lines -match '^PASS ') {
                $lines
                & (Join-Path $PSScriptRoot 'compare.ps1') -SoakRun $soak -SoloReport $env:QY_SMOKE_REPORT -OutputPath (Join-Path $run 'comparison.json')
                return
            }
        }
        if ($proc.HasExited) { throw "Solo process exited early; see $log" }
        Start-Sleep -Seconds 2
    }
    throw "Solo measurement timed out; see $log"
} finally {
    if ($proc -and !$proc.HasExited) { Stop-Process -Id $proc.Id -Force; $proc.WaitForExit() }
    if (Test-Path -LiteralPath $scratch) {
        $owned=Get-CheckedPath $run (Resolve-Path -LiteralPath $scratch).Path
        Remove-Item -LiteralPath $owned -Recurse -Force
    }
}

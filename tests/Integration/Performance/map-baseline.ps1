[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SavePath,
    [Parameter(Mandatory)][string]$GameCopy,
    [ValidateSet('stock','idle','host')][string]$Mode='stock',
    [string]$PackagePath,
    [switch]$DoomedLab,
    [switch]$CaptureJoinSnapshot,
    [ValidateRange(0,60)][int]$WarmupBeforeCaptureSeconds=0,
    [ValidateRange(90,900)][int]$TimeoutSeconds=240
)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
. (Join-Path $repo 'tools/dev-common.ps1')
$qa=(Resolve-Path (Join-Path $repo 'artifacts/qa')).Path
$copy=Get-CheckedPath $qa (Resolve-Path -LiteralPath $GameCopy).Path
$source=(Resolve-Path -LiteralPath $SavePath).Path
if (($Mode -eq 'stock') -eq [bool]$PackagePath) { throw 'Set PackagePath for idle or host mode only.' }
if ($CaptureJoinSnapshot -and $Mode -ne 'host') { throw 'CaptureJoinSnapshot requires host mode.' }
if ($WarmupBeforeCaptureSeconds -and !$CaptureJoinSnapshot) { throw 'WarmupBeforeCaptureSeconds requires CaptureJoinSnapshot.' }
if (Get-Process OxygenNotIncluded -ErrorAction SilentlyContinue) { throw 'Close the running ONI sessions before MAP-01.' }

# Allow only a game copy prepared by the fixture with the isolated profile getter.
$cecil=Get-ChildItem "$env:USERPROFILE/.nuget/packages/microsoft.codecoverage" -Filter Mono.Cecil.dll -Recurse | Select-Object -First 1
[void][Reflection.Assembly]::LoadFrom($cecil.FullName)
$firstpass=Join-Path $copy 'OxygenNotIncluded_Data/Managed/Assembly-CSharp-firstpass.dll'
$module=[Mono.Cecil.ModuleDefinition]::ReadModule($firstpass)
try {
    $getter=@($module.GetType('Util').Methods | Where-Object {$_.Name -ceq 'GetKleiRootPath' -and $_.IsStatic -and !$_.HasParameters})
    if ($getter.Count -ne 1) { throw 'Could not verify the isolated profile getter.' }
    $instructions=@($getter[0].Body.Instructions)
    if ($instructions.Count -ne 3 -or $instructions[0].Operand -cne 'QY_SMOKE_ROOT' -or
        $instructions[1].Operand.FullName -cne 'System.String System.Environment::GetEnvironmentVariable(System.String)' -or
        $instructions[2].OpCode.Name -cne 'ret') { throw 'GameCopy does not have the isolated profile getter.' }
} finally { $module.Dispose() }

$run=Get-CheckedPath $qa (Join-Path $qa ('map01-'+[Guid]::NewGuid().ToString('N')))
$klei=Join-Path $run 'Klei'
$profile=Join-Path $klei 'OxygenNotIncluded'
$mods=Join-Path $profile 'mods/dev'
[IO.Directory]::CreateDirectory($mods) | Out-Null
$project=Join-Path $PSScriptRoot 'SoloPerf.csproj'
dotnet build $project -c Release --nologo "-p:ONI_GAME_DIR=$copy"
if ($LASTEXITCODE) { throw 'Standalone observation driver failed to build.' }
if ($PackagePath) {
    $PackagePath=(Resolve-Path -LiteralPath $PackagePath).Path
    $mod=Join-Path $mods 'QueyangCoop'
    [IO.Compression.ZipFile]::ExtractToDirectory($PackagePath,$mod)
    Assert-DevPackage $mod
    $entry=@{id='QueyangCoop';title='Queyang Co-op (Development)';version=-181413377;staticID=$ModId}
} else {
    $mod=Join-Path $mods 'SoloPerf'
    [IO.Directory]::CreateDirectory($mod) | Out-Null
    $entry=@{id='SoloPerf';title='Solo performance QA';version=-370481578;staticID='Queyang.QA.SoloPerf'}
    [IO.File]::WriteAllLines((Join-Path $mod 'mod.yaml'),@("title: $($entry.title)",'description: Isolated Vanilla observation', "staticID: $($entry.staticID)"))
    Copy-Item -LiteralPath (Join-Path $repo 'src/Game/metadata/mod_info.yaml') -Destination $mod
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net48/SoloPerf.dll') -Destination $mod
$db=@{version=1;mods=@(@{label=@{distribution_platform=4;id=$entry.id;title=$entry.title;version=$entry.version};status=1;enabled=$true;enabledForDlc=@('');crash_count=0;staticID=$entry.staticID})}
[IO.File]::WriteAllText((Join-Path $profile 'mods/mods.json'),($db | ConvertTo-Json -Depth 6))
[IO.File]::WriteAllLines((Join-Path $profile 'kplayerprefs.yaml'),@(
    'strings:', '  SavesDefaultToCloud: Disabled','ints:','  EXPANSION1_ID.ENABLED: 0','  DLC2_ID.ENABLED: 0',
    '  DLC3_ID.ENABLED: 0','  DLC4_ID.ENABLED: 0','  DLC5_ID.ENABLED: 0','  COSMETIC1_ID.ENABLED: 0',
    '  LanguageConfirmationVersion: 2','floats: {}'))
$save=Join-Path $profile 'save_files/MAP01/MAP01.sav'
[IO.Directory]::CreateDirectory((Split-Path $save)) | Out-Null
Copy-Item -LiteralPath $source -Destination $save
$report=Join-Path $run "$Mode.report"
$log=Join-Path $run "$Mode.log"
$before=(Get-FileHash -LiteralPath $source).Hash
$packageHash=if ($PackagePath) { (Get-FileHash -LiteralPath $PackagePath).Hash }
$environment=@{QY_SMOKE_ROOT=$klei;QY_SMOKE_SAVE=$save;QY_SMOKE_REPORT=$report;QY_SMOKE_ROLE='map-baseline';QY_MAP01_MODE=$Mode;QY_MAP01_DOOMED_LAB=[string][int]$DoomedLab.IsPresent;QY_MAP01_CAPTURE=[string][int]$CaptureJoinSnapshot.IsPresent;QY_MAP01_CAPTURE_WARMUP=[string]$WarmupBeforeCaptureSeconds;SteamAppId='457140';SteamGameId='457140'}
$old=@{}
$proc=$null
try {
    foreach ($name in $environment.Keys) {
        $old[$name]=[Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name,$environment[$name])
    }
    Write-Output "MAP-01 evidence directory: $run"
    $proc=Start-Process -FilePath (Join-Path $copy 'OxygenNotIncluded.exe') -WorkingDirectory $copy -ArgumentList @(
        '-screen-width','1280','-screen-height','800','-screen-fullscreen','0','-logFile',('"' + $log + '"')) -WindowStyle Hidden -PassThru
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (Test-Path -LiteralPath $report) {
            $lines=Get-Content -LiteralPath $report
            if ($lines -match '^FAIL ') { throw "MAP-01 observer failed. See $report." }
            $shots=@(Get-ChildItem -LiteralPath $run -Filter "map01-$Mode-*.png" | Where-Object Length -gt 45)
            if (($lines -match "^PASS MAP01_$($Mode.ToUpperInvariant()) ") -and $shots.Count -eq 3) {
                [ordered]@{sourceSha256=$before;mode=$Mode;joinSnapshot=$CaptureJoinSnapshot.IsPresent;warmupSimulationSeconds=$WarmupBeforeCaptureSeconds;packageSha256=$packageHash;screenshots=$shots.Name} |
                    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'input.json') -Encoding UTF8
                $lines
                return
            }
        }
        if ($proc.HasExited) { throw "MAP-01 game exited early. See $log." }
        Start-Sleep -Milliseconds 400
    }
    throw "MAP-01 baseline timed out. See $log."
} finally {
    if ($proc -and !$proc.HasExited) { $proc.Kill(); [void]$proc.WaitForExit(10000) }
    foreach ($name in $old.Keys) { [Environment]::SetEnvironmentVariable($name,$old[$name]) }
    if ((Get-FileHash -LiteralPath $source).Hash -ne $before) { throw 'MAP-01 source save unexpectedly changed.' }
    if ($PackagePath -and (Get-FileHash -LiteralPath $PackagePath).Hash -ne $packageHash) { throw 'MAP-01 package unexpectedly changed.' }
}

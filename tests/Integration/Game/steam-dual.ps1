[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d{17}$')][string]$HostSteamId,
    [Parameter(Mandatory)][ValidatePattern('^\d{17}$')][string]$GuestSteamId,
    [Parameter(Mandatory)][string]$SavePath,
    [Parameter(Mandatory)][string]$PackagePath,
    [string]$GameDir=$env:ONI_GAME_DIR,
    [string]$BoxName='QueyangCoopLoginB',
    [string]$BoxRoot,
    [string]$SandboxStart="$env:ProgramFiles/Sandboxie-Plus/Start.exe",
    [string]$FixtureDriverPath,
    [ValidateRange(800,3840)][int]$GuestWidth=1040,
    [ValidateRange(540,2160)][int]$GuestHeight=650,
    [Alias('MotionBaselineOnly')][switch]$SkipManagement,
    [switch]$SkipPerson,
    [switch]$SkipWork,
    [switch]$CheckBuildings,
    [switch]$CheckConstruction,
    [switch]$CheckAreaDig,
    [switch]$CheckAlert,
    [switch]$CheckSweepSymbol,
    [switch]$CheckResearch,
    [switch]$CheckPower,
    [switch]$CheckBuildingMotion,
    [switch]$CheckResearchMotion,
    [switch]$CheckBattery,
    [switch]$CheckReservoir,
    [switch]$CheckFallingOre,
    [switch]$CheckBuried,
    [switch]$CheckLighting,
    [switch]$CheckHealth,
    [switch]$CheckCritters,
    [switch]$AcceptDeliveredInvite,
    [ValidateRange(0,600)][int]$InviteWaitSeconds=105,
    [ValidateRange(120,3600)][int]$TimeoutSeconds=900
)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
. (Join-Path $repo 'tools/dev-common.ps1')
. (Join-Path $PSScriptRoot 'steam-dual-video.ps1')
$recorders=@{}
$animationRecorders=@{}
$animationCaptures=@()
if($CheckResearchMotion) { $animationCaptures+=@{name='research-motion';marker='RESEARCH_MOTION_RECORD';seconds=8} }
if($CheckBuildingMotion) { $animationCaptures+=@{name='building-motion';marker='BUILDING_MOTION_INPUT';seconds=12} }
if (!$GameDir) {
    $steamRoot=(Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam').SteamPath
    $GameDir=Join-Path $steamRoot 'steamapps/common/OxygenNotIncluded'
}
if (!$BoxRoot) { $BoxRoot=Join-Path "$env:SystemDrive/Sandbox/$env:USERNAME" $BoxName }
if ($HostSteamId -eq $GuestSteamId) { throw 'Two distinct Steam accounts are required.' }
if (Get-Process OxygenNotIncluded -ea 0) { throw 'Close running ONI sessions before this fixture.' }
$BoxRoot=(Resolve-Path -LiteralPath $BoxRoot).Path
$SandboxStart=(Resolve-Path -LiteralPath $SandboxStart).Path
$GameDir=(Resolve-Path -LiteralPath $GameDir).Path
$SavePath=(Resolve-Path -LiteralPath $SavePath).Path
$PackagePath=(Resolve-Path -LiteralPath $PackagePath).Path
$qaRoot=Join-Path $repo 'artifacts/qa'
$run=Get-CheckedPath $qaRoot (Join-Path $qaRoot ('steam-dual-'+[Guid]::NewGuid().ToString('N')))
[IO.Directory]::CreateDirectory($run) | Out-Null
Write-Output "Private run directory: $run"
$sourceDll=Join-Path $GameDir 'OxygenNotIncluded_Data/Managed/Assembly-CSharp-firstpass.dll'
$realMods=Join-Path (Get-DefaultModsRoot) 'mods.json'
$realPrefs=Join-Path (Split-Path (Get-DefaultModsRoot)) 'kplayerprefs.yaml'
$watched=@($PackagePath,$SavePath,$sourceDll,$realMods,$realPrefs)
foreach ($original in $realMods,$realPrefs) {
    if (Test-Path -LiteralPath $original) { Copy-Item -LiteralPath $original -Destination (Join-Path $run ('player-before-'+[IO.Path]::GetFileName($original))) }
}
function Get-InputHashes {
    foreach ($path in $watched) { if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path).Hash } else { 'absent' } }
}
$before=@(Get-InputHashes)
if (!$FixtureDriverPath) {
    $fixtureReferences=Join-Path $run 'package-refs'
    [IO.Compression.ZipFile]::ExtractToDirectory($PackagePath,$fixtureReferences)
    Assert-DevPackage $fixtureReferences
    # Freeze the driver source and all build outputs for this run. A normal developer
    # build may run concurrently and reference a different version of QueyangCoop.
    $fixtureSource=Join-Path $run 'fixture-source'
    [IO.Directory]::CreateDirectory($fixtureSource) | Out-Null
    & robocopy $PSScriptRoot $fixtureSource '*.cs' 'SmokeDriver.csproj' /S /XJ /XD bin obj Peer /R:1 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Could not snapshot fixture source ($LASTEXITCODE)." }
    dotnet build (Join-Path $fixtureSource SmokeDriver.csproj) -c Release "-p:ONI_GAME_DIR=$GameDir" "-p:FixturePackageDir=$fixtureReferences"
    if ($LASTEXITCODE) { throw 'Fixture build failed.' }
    $fixture=Join-Path $fixtureSource 'bin/Release/net48/SmokeDriver.dll'
} else {
    $fixture=(Resolve-Path -LiteralPath $FixtureDriverPath).Path
    if ([reflection.assemblyname]::GetAssemblyName($fixture).Name -ne 'SmokeDriver') { throw 'Expected a pinned SmokeDriver fixture DLL.' }
}
$cecil=Get-ChildItem "$env:USERPROFILE/.nuget/packages/microsoft.codecoverage" -Filter Mono.Cecil.dll -Recurse | Select-Object -First 1
[void][Reflection.Assembly]::LoadFrom($cecil.FullName)
function Copy-PrivateGame([string]$from,[string]$to) {
    # Share private read-only copies of large art/audio assets between fixture runs.
    # All binaries, configuration and writable files still get independent copies.
    $patterns=@('*.resS','*.assets','*.resource','*.bank')
    & robocopy $from $to /E /XJ /R:1 /W:0 /NFL /NDL /NJH /NJS /NP /XF $patterns | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Private game copy failed ($LASTEXITCODE)." }
    $cache=Join-Path $qaRoot 'readonly-oni-assets'
    [IO.Directory]::CreateDirectory($cache) | Out-Null
    foreach ($asset in Get-ChildItem -LiteralPath $from -File -Recurse | Where-Object {$_.Extension -in '.resS','.assets','.resource','.bank'}) {
        $digest=(Get-FileHash -LiteralPath $asset.FullName).Hash
        $cached=Join-Path $cache $digest
        if (!(Test-Path -LiteralPath $cached)) {
            $temp=Join-Path $cache ([Guid]::NewGuid().ToString('N'))
            [IO.File]::Copy($asset.FullName,$temp)
            if ((Get-FileHash -LiteralPath $temp).Hash -ne $digest) { throw 'Game asset changed during copy.' }
            [IO.File]::SetAttributes($temp,[IO.FileAttributes]::ReadOnly)
            [IO.File]::Move($temp,$cached)
        }
        if ((Get-FileHash -LiteralPath $cached).Hash -ne $digest) { throw "Corrupt QA asset cache entry $digest" }
        $dest=Join-Path $to $asset.FullName.Substring($from.Length+1)
        New-Item -ItemType HardLink -Path $dest -Target $cached | Out-Null
    }
}
$copy=Join-Path $run 'host-game'
Copy-PrivateGame $GameDir $copy
$managed=Get-CheckedPath $copy (Join-Path $copy 'OxygenNotIncluded_Data/Managed')
$firstpass=Get-CheckedPath $managed (Join-Path $managed 'Assembly-CSharp-firstpass.dll')
$resolver=New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($managed)
$reader=New-Object Mono.Cecil.ReaderParameters
$reader.AssemblyResolver=$resolver
$reader.InMemory=$true
$module=[Mono.Cecil.ModuleDefinition]::ReadModule($firstpass,$reader)
try {
    $method=@($module.GetType('Util').Methods | Where-Object {$_.Name -ceq 'GetKleiRootPath' -and $_.IsStatic -and !$_.HasParameters})
    if ($method.Count -ne 1 -or $method[0].ReturnType.FullName -ne 'System.String') { throw 'Unrecognized Klei profile getter.' }
    $body=$method[0].Body
    $body.Instructions.Clear()
    $body.ExceptionHandlers.Clear()
    $il=$body.GetILProcessor()
    $getter=$module.ImportReference([Environment].GetMethod('GetEnvironmentVariable',[type[]]@([string])))
    $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldstr,'QY_SMOKE_ROOT')
    $il.Emit([Mono.Cecil.Cil.OpCodes]::Call,$getter)
    $il.Emit([Mono.Cecil.Cil.OpCodes]::Ret)
    $module.Write("$firstpass.isolated")
} finally { $module.Dispose(); $resolver.Dispose() }
[IO.File]::Replace("$firstpass.isolated",$firstpass,[System.Management.Automation.Language.NullString]::Value)

Copy-PrivateGame $copy (Join-Path $run 'guest-game')
$roles=@('host','guest')
$drivers=@{}
foreach ($role in $roles) {
    $base=Join-Path $run $role
    $klei=Join-Path $base 'Klei'
    $profile=Join-Path $klei 'OxygenNotIncluded'
    $mods=Join-Path $profile 'mods'
    $mod=Join-Path $mods 'dev/QueyangCoop'
    [IO.Directory]::CreateDirectory($mod) | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($PackagePath,$mod)
    Assert-DevPackage $mod
    Copy-Item -LiteralPath $fixture -Destination (Join-Path $mod 'SmokeDriver.dll')
    $modDb=@{version=1;mods=@(@{label=@{distribution_platform=4;id='QueyangCoop';title='Queyang Co-op (Development)';version=-181413377};status=1;enabled=$true;enabledForDlc=@('');crash_count=0;reinstall_path=$null;staticID=$ModId})}
    [IO.File]::WriteAllText((Join-Path $mods 'mods.json'),($modDb|ConvertTo-Json -Depth 6))
    $prefs="strings:`n  SavesDefaultToCloud: Disabled`nints:`n  EXPANSION1_ID.ENABLED: 0`n  DLC2_ID.ENABLED: 0`n  DLC3_ID.ENABLED: 0`n  DLC4_ID.ENABLED: 0`n  DLC5_ID.ENABLED: 0`n  COSMETIC1_ID.ENABLED: 0`n  LanguageConfirmationVersion: 2`nfloats: {}`n"
    [IO.File]::WriteAllText((Join-Path $profile 'kplayerprefs.yaml'),$prefs)
    $save=Join-Path $profile 'save_files/Fixture/Fixture.sav'
    if ($role -eq 'host') {
        [IO.Directory]::CreateDirectory((Split-Path $save)) | Out-Null
        Copy-Item -LiteralPath $SavePath -Destination $save
    }
    $self=if ($role -eq 'host') { $HostSteamId } else { $GuestSteamId }
    $friend=if ($role -eq 'host') { $GuestSteamId } else { $HostSteamId }
    $exe=Join-Path $run "$role-game/OxygenNotIncluded.exe"
    $values=[ordered]@{
        SteamAppId='457140'; SteamGameId='457140'; QY_BASELINE_ONLY=([int]$SkipManagement.IsPresent).ToString(); QY_SKIP_PERSON=([int]$SkipPerson.IsPresent).ToString(); QY_SKIP_WORK=([int]$SkipWork.IsPresent).ToString(); QY_SMOKE_ROLE=$role; QY_DUAL_ROLE=$role
        QY_WINDOW_WIDTH=$(if($role -eq 'guest'){$GuestWidth}else{1280}); QY_WINDOW_HEIGHT=$(if($role -eq 'guest'){$GuestHeight}else{800})
        QY_SMOKE_REPORT=(Join-Path $base "$role.report"); QY_SMOKE_ROOT=$klei; QY_SMOKE_SAVE=$save
        QY_DUAL_SELF=$self; QY_DUAL_FRIEND=$friend; QY_DUAL_HOST_ID=$HostSteamId; QY_DUAL_WAIT_INVITE_SECONDS=([string]$InviteWaitSeconds); QY_DUAL_ACCEPT_INVITE=([int]$AcceptDeliveredInvite.IsPresent).ToString()
    }
    $values.QY_CHECK_BUILDINGS=([int]$CheckBuildings.IsPresent).ToString()
    $values.QY_CONSTRUCTION_RANGE=([int]$CheckConstruction.IsPresent).ToString()
    $values.QY_AREA_DIG_RANGE=([int]$CheckAreaDig.IsPresent).ToString()
    $values.QY_ALERT_RANGE=([int]$CheckAlert.IsPresent).ToString()
    $values.QY_SWEEP_RANGE=([int]$CheckSweepSymbol.IsPresent).ToString()
    $values.QY_RESEARCH_RANGE=([int]$CheckResearch.IsPresent).ToString()
    $values.QY_POWER_RANGE=([int]$CheckPower.IsPresent).ToString()
    $values.QY_BUILDING_MOTION_RANGE=([int]$CheckBuildingMotion.IsPresent).ToString()
    $values.QY_RESEARCH_MOTION_RANGE=([int]$CheckResearchMotion.IsPresent).ToString()
    $values.QY_BATTERY_RANGE=([int]$CheckBattery.IsPresent).ToString()
    $values.QY_RESERVOIR_RANGE=([int]$CheckReservoir.IsPresent).ToString()
    $values.QY_CHECK_FALLING_ORE=([int]$CheckFallingOre.IsPresent).ToString()
    $values.QY_CHECK_BURIED=([int]$CheckBuried.IsPresent).ToString()
    $values.QY_CHECK_LIGHTING=([int]$CheckLighting.IsPresent).ToString()
    $values.QY_CHECK_HEALTH=([int]$CheckHealth.IsPresent).ToString()
    $values.QY_CHECK_CRITTERS=([int]$CheckCritters.IsPresent).ToString()
    $drivers[$role]=[pscustomobject]@{role=$role;exe=$exe;env=$values;mods=$mods;base=$base;restarts=0;started=$false;done=$false;wrapper=$null}
    [ordered]@{ role=$role;package=$before[0];sourceSave=$before[1];fixture=(Get-FileHash $fixture).Hash
        game=(Get-FileHash (Join-Path $mod 'QueyangCoop.dll')).Hash;core=(Get-FileHash (Join-Path $mod 'Queyang.Core.dll')).Hash
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $base 'input.json')
}
# The guest wrapper reads its environment inside the box. Start.exe does not need
# to inherit custom variables, and account identifiers never enter the ONI command line.
$wrapper=Join-Path $run 'launch.ps1'
@"
param([string]`$Base,[string]`$Exe)
`$ErrorActionPreference='Stop'
`$settings=Get-Content -LiteralPath (Join-Path `$Base 'session.json') -Raw | ConvertFrom-Json
Get-ChildItem Env: | Where-Object { `$_.Name -like 'QY_*' -or `$_.Name -eq 'ONI_COOP_BIND' } | ForEach-Object { [Environment]::SetEnvironmentVariable(`$_.Name,`$null) }
foreach (`$prop in `$settings.PSObject.Properties) { [Environment]::SetEnvironmentVariable(`$prop.Name,[string]`$prop.Value) }
`$playerArgs=@('-screen-width',`$env:QY_WINDOW_WIDTH,'-screen-height',`$env:QY_WINDOW_HEIGHT,'-screen-fullscreen','0','-logFile',('"'+(Join-Path `$Base 'player.log')+'"'))
`$p=Start-Process -FilePath `$Exe -WorkingDirectory (Split-Path `$Exe) -ArgumentList `$playerArgs -WindowStyle Hidden -PassThru
[IO.File]::WriteAllText((Join-Path `$Base 'player.pid'),[string]`$p.Id)
`$p.WaitForExit()
exit `$p.ExitCode
"@ | Set-Content -LiteralPath $wrapper
function Get-Physical([object]$driver,[string]$virtualPath) {
    if ($driver.role -eq 'guest') {
        $drive=[IO.Path]::GetPathRoot($virtualPath).Substring(0,1)
        $cow=Get-CheckedPath $BoxRoot (Join-Path $BoxRoot ("drive/$drive/"+$virtualPath.Substring(3)))
        if (Test-Path -LiteralPath $cow) { return $cow }
    }
    $virtualPath
}
function Start-Player([object]$driver) {
    $driver.env | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $driver.base 'session.json') -Encoding UTF8
    $params=@('-NoLogo','-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $wrapper + '"'),('"' + $driver.base + '"'),('"' + $driver.exe + '"'))
    $shell=Join-Path $PSHOME 'powershell.exe'
    if (!(Test-Path $shell)) { $shell="$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" }
    if ($driver.role -eq 'guest') {
        $params=@("/box:$BoxName",'/silent','/wait',('"' + $shell + '"')) + $params
        $shell=$SandboxStart
    }
    $driver.wrapper=Start-Process -FilePath $shell -ArgumentList $params -WindowStyle Hidden -PassThru
    $driver.started=$true
}
function Get-Player([object]$driver) {
    $pidPath=Get-Physical $driver (Join-Path $driver.base 'player.pid')
    if (Test-Path $pidPath) {
        $gamePid=0
        if (![int]::TryParse([string](Get-Content $pidPath -ea 0),[ref]$gamePid)) { return }
        $player=Get-Process -Id $gamePid -ea 0
        if ($player -and $player.ProcessName -eq 'OxygenNotIncluded' -and
            ($player.Path -eq $driver.exe -or $player.Path -eq (Get-Physical $driver $driver.exe))) { return $player }
    }
}
function Restart-Player([object]$driver,[string]$report) {
    if ($driver.restarts -ge 1) { throw "Repeated mods restart for $($driver.role)." }
    $p=Get-Player $driver
    if ($p -and !$p.WaitForExit(20000)) { throw 'ONI failed to exit on its requested restart.' }
    $db=Get-Physical $driver (Join-Path $driver.mods 'mods.json')
    $settings=Get-Content -Raw -LiteralPath $db | ConvertFrom-Json
    $entry=@($settings.mods | Where-Object { $_.label.id -ceq 'QueyangCoop' -and $_.label.distribution_platform -eq 4 })
    if ($entry.Count -ne 1) { throw 'Unknown isolated mod database after restart.' }
    $entry[0].enabled=$true
    $entry[0].enabledForDlc=@('')
    [IO.File]::WriteAllText($db,($settings | ConvertTo-Json -Depth 12))
    Move-Item -LiteralPath $report -Destination (Join-Path $driver.base 'first-launch.report')
    $log=Get-Physical $driver (Join-Path $driver.base 'player.log')
    if (Test-Path $log) { Move-Item -LiteralPath $log -Destination (Join-Path $driver.base 'first-launch.log') }
    $driver.restarts++
    Start-Player $driver
}
# The Steam cloud client can also see the user's real ONI settings. Share them
# read-only during isolated play and readback so their original bytes survive a concurrent write.
$guards=@()
try {
foreach ($path in $realMods,$realPrefs) {
    if (Test-Path -LiteralPath $path) {
        $guards+=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    }
}
try {
    Start-Player $drivers.host
    $notified=@{}
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        foreach ($role in $roles) {
            $driver=$drivers[$role]
            if (!$driver.started -or $driver.done) { continue }
            $report=Get-Physical $driver $driver.env.QY_SMOKE_REPORT
            $lines=@(if (Test-Path $report) { Get-Content -LiteralPath $report })
            if ($role -eq 'host' -and !$recorders.Count -and $lines -match '^DUAL_MOTION_(ARM|START)\b') {
                foreach ($endpoint in $roles) { $recorders[$endpoint]=Start-DualRecording $drivers[$endpoint] }
            }
            if ($role -eq 'host') {
                foreach ($capture in $animationCaptures) {
                    if ($animationRecorders.ContainsKey($capture.name) -or !($lines -match "^$($capture.marker) ")) { continue }
                    $animationRecorders[$capture.name]=@{}
                    foreach ($endpoint in $roles) {
                        $animationRecorders[$capture.name][$endpoint]=Start-DualRecording $drivers[$endpoint] $capture.name $capture.seconds
                    }
                }
            }
            if ($role -eq 'host' -and $recorders.Count -eq 2 -and !$motionReady) {
                $files=@(foreach ($endpoint in $roles) {
                    $out=Join-Path $drivers[$endpoint].base 'dupe-motion.mp4'
                    if ((Test-Path $out) -and (Get-Item $out).Length -gt 0) { $out }
                    elseif ($recorders[$endpoint].HasExited) { throw "Capture failed to initialize for $endpoint; see capture.log" }
                })
                if ($files.Count -eq 2) {
                    [IO.File]::WriteAllText((Join-Path $driver.base 'motion-recording.ready'),'ready')
                    $motionReady=$true
                }
            }
            if ($role -eq 'guest') {
                foreach ($round in 1,2) {
                    if (!$notified[$round] -and $lines -match "^DUAL_INVITE_RECEIVED round=$round\b") {
                        $notified[$round]=$true
                        Write-Output "Round $round invitation arrived in the guest Steam client."
                    }
                }
            }
            if ($lines -match '^FAIL ') { throw "Game reported failure in $role. See private report." }
            if ($lines -match '^NATIVE_RESTART_REQUIRED') { Restart-Player $driver $report; continue }
            if ($role -eq 'host' -and !$drivers.guest.started) {
                $mark=@($lines -match '^DUAL_HOST_LOBBY [0-9]+$')
                if ($mark.Count) {
                    $drivers.guest.env.QY_DUAL_LOBBY=$mark[-1].Split(' ')[1]
                    Start-Player $drivers.guest
                }
            }
            if ($lines -match "^PASS DUAL_$($role.ToUpperInvariant())\b") { $driver.done=$true; continue }
            if ($driver.wrapper.HasExited) { throw "$role launcher exited early ($($driver.wrapper.ExitCode))." }
        }
        if ($drivers.host.done -and $drivers.guest.done) { break }
        Start-Sleep -Milliseconds 400
    }
    if (!$drivers.host.done -or !$drivers.guest.done) { throw 'Two-account Steam fixture timed out. See private reports.' }
    foreach ($endpoint in $roles) { Confirm-DualRecording $drivers[$endpoint] $recorders[$endpoint] }
    foreach ($capture in $animationCaptures) {
        if (!$animationRecorders.ContainsKey($capture.name)) { throw "Missing $($capture.name) recording start." }
        foreach ($endpoint in $roles) {
            Confirm-DualRecording $drivers[$endpoint] $animationRecorders[$capture.name][$endpoint] $capture.name $capture.seconds
        }
    }
    foreach ($endpoint in $roles) {
        $d=$drivers[$endpoint]
        $report=Get-Physical $d $d.env.QY_SMOKE_REPORT
        foreach ($pattern in '^DUAL_MATCHED_RENDER ','^DUAL_DIG_TINTS ') {
            if (!(Select-String -LiteralPath $report -Pattern $pattern -Quiet)) { throw "Missing $endpoint evidence for $pattern." }
        }
        $shot=Get-Physical $d (Join-Path $d.base "matched-$endpoint.png")
        if (!(Test-Path $shot) -or (Get-Item $shot).Length -lt 45) { throw "Missing $endpoint matched scene screenshot." }
    }
    foreach ($evidence in @(@('host','^DUAL_HOST_LOCAL_ORDER '),@('guest','^DUAL_GUEST_HOST_ORDER '),
        @('host','^DUAL_ORDER_OWNERS .*bothTooltipsRepainted=True$'),@('guest','^DUAL_ORDER_OWNERS .*bothTooltipsRepainted=True$'),
        @('host','^DUAL_LEGACY_ORDER '),@('guest','^DUAL_LEGACY_ORDER '))) {
        $d=$drivers[$evidence[0]]
        if (!(Select-String -LiteralPath (Get-Physical $d $d.env.QY_SMOKE_REPORT) -Pattern $evidence[1] -Quiet)) {
            throw "Missing required evidence in $($d.role) ($($evidence[1]))."
        }
    }
    foreach ($entry in @(@('host','entry-host-1.png'),@('guest','entry-guest-1.png'),@('host','entry-host-2.png'),@('guest','entry-guest-2.png'),@('host','dual-host.png'),@('host','dual-host-2.png'),@('guest','dual-guest-dig.png'),@('guest','dual-guest-resources.png'),@('guest','dual-guest-1.png'),@('guest','dual-guest-2.png'))) {
        $d=$drivers[$entry[0]]
        $path=Get-Physical $d (Join-Path $d.base $entry[1])
        if (!(Test-Path $path) -or (Get-Item $path).Length -lt 45) { throw "Missing screenshot $($entry[1])." }
    }
    if ($CheckConstruction) {
        foreach($role in $roles) {
            $d=$drivers[$role]
            $report=Get-Physical $d $d.env.QY_SMOKE_REPORT
            foreach($stage in 0..2) {
                if (!(Select-String -LiteralPath $report -Pattern "^CONSTRUCTION_RANGE stage=$stage " -Quiet)) {
                    throw "Missing native construction checkpoint $stage for $role."
                }
                $shot=Get-Physical $d (Join-Path $d.base "range-$role-$stage.png")
                if (!(Test-Path -LiteralPath $shot)) { throw "Missing construction screenshot $shot." }
            }
        }
    }
    if($CheckAreaDig) {
        foreach($role in $roles) {
            $d=$drivers[$role]
            $report=Get-Physical $d $d.env.QY_SMOKE_REPORT
            foreach($marker in 'ORDERS','BAR','BAR_CLEARED') {
                if(!(Select-String -LiteralPath $report -Pattern "^AREA_DIG_$marker " -Quiet)) {
                    throw "Missing area excavation checkpoint $marker for $role."
                }
            }
            foreach($stage in 'orders','work') {
                if(!(Test-Path -LiteralPath (Get-Physical $d (Join-Path $d.base "area-$role-$stage.png")))) {
                    throw "Missing area excavation screenshot $stage for $role."
                }
            }
        }
    }
    foreach($case in @(@($CheckAlert,'alert'),@($CheckSweepSymbol,'sweep'),@($CheckPower,'power'),@($CheckResearch,'research'),@($CheckBattery,'battery'),@($CheckReservoir,'reservoir'))) {
        if(!$case[0]) {continue}
        $suite=$case[1]
        $record=$suite.ToUpperInvariant()+'_RANGE'
        foreach($role in $roles) {
            $d=$drivers[$role]
            $report=Get-Physical $d $d.env.QY_SMOKE_REPORT
            foreach($stage in 0..2) {
                if (!(Select-String -LiteralPath $report -Pattern "^$record stage=$stage " -Quiet)) {
                    throw "Missing $suite checkpoint $stage for $role."
                }
                $shot=Get-Physical $d (Join-Path $d.base "$suite-$role-$stage.png")
                if (!(Test-Path -LiteralPath $shot)) { throw "Missing $suite screenshot $shot." }
            }
        }
    }
    foreach ($case in @(@($CheckBuildingMotion,'building-motion','BUILDING_MOTION_RANGE'),@($CheckResearchMotion,'research-motion','RESEARCH_MOTION_RANGE'))) {
        if (!$case[0]) { continue }
        foreach ($role in $roles) {
            $d=$drivers[$role]
            $report=Get-Physical $d $d.env.QY_SMOKE_REPORT
            if (!(Select-String -LiteralPath $report -Pattern "^$($case[2]) role=$role " -Quiet)) {
                throw "Missing $($case[1]) checkpoint for $role."
            }
            foreach ($ext in 'png','csv') {
                $dest=Join-Path $d.base "$($case[1])-$role.$ext"
                $src=Get-Physical $d $dest
                if (!(Test-Path -LiteralPath $src)) { throw "Missing $($case[1]) $ext for $role." }
                if ($src -ne $dest) { Copy-Item -LiteralPath $src -Destination $dest }
            }
        }
    }
    if ($CheckBuried) {
        foreach ($role in $roles) {
            $d=$drivers[$role]
            $report=Get-Physical $d $d.env.QY_SMOKE_REPORT
            if (!(Select-String -LiteralPath $report -Pattern '^DUAL_BURIED_RELEASE .* markerVisible=False productVisible=True$' -Quiet)) {
                throw "Missing native buried-item release evidence for $role."
            }
            foreach ($step in 'before','after') {
                $file=Get-Physical $d (Join-Path $d.base "buried-$role-$step.png")
                if (!(Test-Path $file) -or (Get-Item $file).Length -lt 45) { throw "Missing buried-item screenshot $role $step." }
            }
        }
        foreach ($step in 'before','after') {
            $a=$drivers.host;$b=$drivers.guest
            & (Join-Path $PSScriptRoot 'compare-buried.ps1') -HostPng (Get-Physical $a (Join-Path $a.base "buried-host-$step.png")) -GuestPng (Get-Physical $b (Join-Path $b.base "buried-guest-$step.png"))
        }
    }
    if ($CheckFallingOre) {
        foreach ($role in $roles) {
            $d=$drivers[$role]
            if (!(Select-String -LiteralPath (Get-Physical $d $d.env.QY_SMOKE_REPORT) -Pattern '^DUAL_FALLING_ORE .* kg=12001 visible=1$' -Quiet)) {
                throw "Missing falling ore motion, large mass and landing evidence for $role."
            }
        }
    }
    if ($CheckCritters) {
        foreach ($role in $roles) {
            $d=$drivers[$role]
            foreach ($leaf in 'critter-motion.csv',"critter-$role.png") {
                $file=Get-Physical $d (Join-Path $d.base $leaf)
                if (!(Test-Path $file) -or (Get-Item $file).Length -lt 45) { throw "Missing $role critter evidence $leaf." }
            }
        }
    }
    if ($CheckBuildings) {
        foreach ($role in $roles) {
            $d=$drivers[$role]
            $lines=Get-Content -LiteralPath (Get-Physical $d $d.env.QY_SMOKE_REPORT)
            foreach ($stage in 0..3) {
                $file=Get-Physical $d (Join-Path $d.base "building-$role-$stage.png")
                if (!($lines -match "^DUAL_STORAGE_RENDERED stage=$stage ") -or !(Test-Path $file) -or (Get-Item $file).Length -lt 45) {
                    throw "Missing rendered building panel evidence for $role stage $stage."
                }
            }
        }
    }
    if (!$SkipManagement) {
        foreach ($role in $roles) {
            $d=$drivers[$role]
            $lines=Get-Content -LiteralPath (Get-Physical $d $d.env.QY_SMOKE_REPORT)
            if (!$SkipPerson -and !($lines -match "^DUAL_$($role.ToUpperInvariant())_PERSON_CONFIRMED")) { throw "Missing $role newcomer verification" }
            $sections=if ($SkipWork) { @('skills') } else { @('skills','schedule','priorities') }
            foreach ($section in $sections) {
                $pattern=if ($section -eq 'priorities') { 'PRIORITY' } else { $section.ToUpperInvariant().TrimEnd('S') }
                if (!($lines -match "^DUAL_$($role.ToUpperInvariant())_$pattern")) { throw "Missing $role $section check." }
                $pictures=if ($section -eq 'skills') { @("dual-$role-skills.png") } else { @("dual-$role-$section-1.png","dual-$role-$section-2.png") }
                foreach ($leaf in $pictures) {
                    $png=Get-Physical $d (Join-Path $d.base $leaf)
                    if (!(Test-Path $png) -or (Get-Item $png).Length -lt 45) { throw "Missing $leaf" }
                }
            }
        }
    }
} catch {
    $fixtureError=$_
    throw
} finally {
    foreach ($p in $recorders.Values) { if ($p -and !$p.HasExited) { $p.Kill(); [void]$p.WaitForExit(10000) } }
    foreach ($pair in $animationRecorders.Values) {
        foreach ($p in $pair.Values) { if ($p -and !$p.HasExited) { $p.Kill(); [void]$p.WaitForExit(10000) } }
    }
    foreach ($role in $roles) {
        $d=$drivers[$role]
        $p=Get-Player $d
        if ($p -and !$p.HasExited) { $p.Kill(); [void]$p.WaitForExit(10000) }
        if ($role -eq 'guest') {
            if($CheckAreaDig) {
                foreach($stage in 'orders','work'){
                    $dest=Join-Path $d.base "area-guest-$stage.png"
                    $src=Get-Physical $d $dest
                    if($src -ne $dest -and (Test-Path $src)){Copy-Item -LiteralPath $src -Destination $dest}
                }
            }
            foreach($case in @(@($CheckAlert,'alert'),@($CheckSweepSymbol,'sweep'),@($CheckPower,'power'),@($CheckResearch,'research'),@($CheckBattery,'battery'),@($CheckReservoir,'reservoir'))) {
                if(!$case[0]) {continue}
                $suite=$case[1]
                foreach($stage in 0..2){
                    $dest=Join-Path $d.base "$suite-guest-$stage.png"
                    $src=Get-Physical $d $dest
                    if($src -ne $dest -and (Test-Path $src)) { Copy-Item -LiteralPath $src -Destination $dest }
                }
            }
            if($CheckConstruction) {
                foreach($stage in 0..2){
                    $dest=Join-Path $d.base "range-guest-$stage.png"
                    $src=Get-Physical $d $dest
                    if ($src -ne $dest -and (Test-Path $src)) { Copy-Item -LiteralPath $src -Destination $dest }
                }
            }
            if($CheckHealth) {
                foreach($stage in 0..2){
                    $dest=Join-Path $d.base "health-guest-$stage.png"
                    $src=Get-Physical $d $dest
                    if ($src -ne $dest -and (Test-Path $src)) { Copy-Item -LiteralPath $src -Destination $dest }
                }
            }
            if($CheckLighting) {
                foreach($stage in 0..2){
                    $dest=Join-Path $d.base "light-guest-$stage.png"
                    $src=Get-Physical $d $dest
                    if ($src -ne $dest -and (Test-Path $src)) { Copy-Item -LiteralPath $src -Destination $dest }
                }
            }
            foreach ($leaf in @('buried-guest-before.png','buried-guest-after.png','drill.csv','dual-guest-drill.png','critter-motion.csv','critter-guest.png')+@(0..7 | ForEach-Object { "hat-guest-$_.png" })) {
                $dest=Join-Path $d.base $leaf
                $src=Get-Physical $d $dest
                if ($src -ne $dest -and (Test-Path $src)) { Copy-Item -LiteralPath $src -Destination $dest }
            }
            foreach ($stage in 'printed','rejoined','death') {
                $dest=Join-Path $d.base "dual-guest-population-$stage.png"
                $src=Get-Physical $d $dest
                if ($src -ne $dest -and (Test-Path $src)) { Copy-Item -LiteralPath $src -Destination $dest }
            }
            if ($CheckBuildings) {
                foreach ($stage in 0..3) {
                    $dest=Join-Path $d.base "building-guest-$stage.png"
                    $src=Get-Physical $d $dest
                    if ($src -ne $dest -and (Test-Path $src)) { Copy-Item -LiteralPath $src -Destination $dest }
                }
            }
            foreach ($leaf in @('guest.report','player.log','matched-guest.png','dual-guest-person.png','dual-guest-skills.png','dual-guest-schedule-1.png','dual-guest-schedule-2.png','dual-guest-priorities-1.png','dual-guest-priorities-2.png','entry-guest-1.png','entry-guest-2.png','dupe-motion.csv','dig-fx.csv','dual-guest-dig.png','dual-guest-resources.png','dual-guest-1.png','dual-guest-2.png')) {
                $dest=Join-Path $d.base $leaf
                $src=Get-Physical $d $dest
                if ($src -ne $dest -and (Test-Path $src)) { Copy-Item -LiteralPath $src -Destination $dest }
            }
        }
    }
    foreach ($original in $realMods,$realPrefs) {
        if (Test-Path -LiteralPath $original) { Copy-Item -LiteralPath $original -Destination (Join-Path $run ('player-after-'+[IO.Path]::GetFileName($original))) }
    }
    if (($before -join '/') -ne (@(Get-InputHashes) -join '/')) {
        if ($fixtureError) { Write-Warning 'A protected input also changed during the fixture.' }
        else { throw 'A protected input changed during the fixture.' }
    }
}
$reader=Join-Path $PSScriptRoot 'persistence.ps1'
& $reader -RunRoot $run -GameCopy $copy -WithOwnershipPackage $PackagePath
& $reader -RunRoot $run -GameCopy $copy
if (($before -join '/') -ne (@(Get-InputHashes) -join '/')) { throw 'A protected input changed during save readback.' }
$guestReport=Get-Content -LiteralPath (Join-Path $drivers.guest.base 'guest.report')
$joins=@(foreach ($line in $guestReport) {
    if ($line -match '^DUAL_GUEST_JOIN round=(\d+) method=(\S+) delivered=(True|False) joinRequested=(True|False) ') {
        [ordered]@{round=[int]$Matches[1];method=$Matches[2];delivered=[bool]::Parse($Matches[3]);joinRequested=[bool]::Parse($Matches[4])}
    }
})
if ($joins.Count -ne 2 -or $joins[0].round -ne 1 -or $joins[1].round -ne 2) { throw 'Missing exact join methods for both rounds.' }
[ordered]@{packageSha256=$before[0];saveSha256=$before[1];protectedInputsUnchanged=$true;joins=$joins
    checks=[ordered]@{areaDig=$CheckAreaDig.IsPresent;alert=$CheckAlert.IsPresent;sweepSymbol=$CheckSweepSymbol.IsPresent;power=$CheckPower.IsPresent;research=$CheckResearch.IsPresent;buildingMotion=$CheckBuildingMotion.IsPresent;researchMotion=$CheckResearchMotion.IsPresent;battery=$CheckBattery.IsPresent;reservoir=$CheckReservoir.IsPresent;construction=$CheckConstruction.IsPresent;health=$CheckHealth.IsPresent;lighting=$CheckLighting.IsPresent;buildings=$CheckBuildings.IsPresent;buried=$CheckBuried.IsPresent;fallingOre=$CheckFallingOre.IsPresent;critters=$CheckCritters.IsPresent;skills=!$SkipManagement;person=!$SkipManagement -and !$SkipPerson;work=!$SkipManagement -and !$SkipWork}} |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding UTF8
Write-Output "PASS both Steam game clients; evidence in $run"
} finally {
    foreach ($guard in $guards) { $guard.Dispose() }
}

# Opt-in integration fixture. Build a private game copy; never point the patcher at the Steam install.
[CmdletBinding()]
param([string]$SavePath,[string]$GameDir=$env:ONI_GAME_DIR,[int]$TimeoutSeconds=160,[switch]$SecondGame,[switch]$GuestOnly,[switch]$SteamLobby,[switch]$Persistence,[ValidateRange(0,86400)][int]$SoakSeconds=0,[switch]$Lifecycle,[switch]$LifecycleScreenshot,[switch]$SoloPerformance,[switch]$NativeRecord,[string]$NativeReplay,[string]$NativeEndpoint)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
. (Join-Path $repo 'tools/dev-common.ps1')
if (!$GameDir) {
    $steamRoot=(Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam').SteamPath
    $GameDir=Join-Path $steamRoot 'steamapps/common/OxygenNotIncluded'
}
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
if (!$GuestOnly -and !$NativeReplay -and !$NativeEndpoint) {
    if (!$SavePath) { throw 'Provide -SavePath for the disposable host, or -GuestOnly for the Unity guest check.' }
    $SavePath = (Resolve-Path -LiteralPath $SavePath).Path
    if ([IO.Path]::GetExtension($SavePath) -ine '.sav') { throw 'Select a Vanilla .sav for the disposable host.' }
}
if ($SecondGame -and $GuestOnly) { throw 'Select either a simultaneous second game or the guest-only test.' }
if ($SteamLobby -and ($GuestOnly -or $SecondGame -or $Persistence)) { throw '-SteamLobby runs only the real single-host Steam test.' }
if ($Persistence -and $GuestOnly) { throw '-Persistence needs a host save.' }
if ($LifecycleScreenshot -and !$Lifecycle) { throw 'Use -LifecycleScreenshot together with -Lifecycle.' }
if ($Lifecycle -and ($SecondGame -or $GuestOnly -or $SteamLobby -or $Persistence -or $SoakSeconds)) { throw '-Lifecycle requires its own disposable guest and save.' }
if ($SoloPerformance -and ($SecondGame -or $GuestOnly -or $SteamLobby -or $Persistence -or $Lifecycle -or $SoakSeconds)) { throw '-SoloPerformance runs only the isolated single-player measurement.' }
if ($Lifecycle) { $TimeoutSeconds=[Math]::Max($TimeoutSeconds,360) }
if ($NativeEndpoint -and ($NativeRecord -or $NativeReplay -or $Lifecycle -or $SoakSeconds -or $Persistence -or $SoloPerformance -or $SecondGame -or $GuestOnly -or $SteamLobby)) { throw 'NativeEndpoint uses only the isolated guest and a running external LAN host.' }
if (($NativeRecord -or $NativeReplay) -and ($GuestOnly -or $SteamLobby -or $SecondGame -or ($NativeReplay -and $Persistence) -or $Lifecycle -or $SoakSeconds -or $SoloPerformance -or ($NativeRecord -and $NativeReplay))) { throw 'Run native recording and replay separately.' }
$guestFixture=$GuestOnly -or $Lifecycle -or $NativeReplay
if ($SoakSeconds -and ($GuestOnly -or $SteamLobby -or $SecondGame -or $Persistence)) { throw '-SoakSeconds uses one real host and three TCP guest processes/peers.' }
if ($SoakSeconds -gt 0) { $TimeoutSeconds=[Math]::Max($TimeoutSeconds,$SoakSeconds + 90) }
if (!(Test-Path -LiteralPath (Join-Path $GameDir OxygenNotIncluded.exe))) { throw 'Set ONI_GAME_DIR to your Steam install.' }
if (Get-Process OxygenNotIncluded -ErrorAction SilentlyContinue) { throw 'Close active ONI sessions before this test.' }
$initialSaveHash = if (!$GuestOnly -and !$NativeReplay -and !$NativeEndpoint) { (Get-FileHash -LiteralPath $SavePath).Hash }
$realDb=Join-Path (Get-DefaultModsRoot) 'mods.json'
$initialDbHash=if (Test-Path -LiteralPath $realDb) { (Get-FileHash -LiteralPath $realDb).Hash }
$sourceDll=Join-Path $GameDir 'OxygenNotIncluded_Data/Managed/Assembly-CSharp-firstpass.dll'
$initialDllHash=(Get-FileHash -LiteralPath $sourceDll).Hash
dotnet build (Join-Path $PSScriptRoot SmokeDriver.csproj) -c Release "-p:ONI_GAME_DIR=$GameDir"
if ($LASTEXITCODE) { throw 'Fixture or game project build failed.' }
if (!$SecondGame -and !$SteamLobby) {
    dotnet build (Join-Path $PSScriptRoot 'Peer/Peer.csproj') -c Release "-p:ONI_GAME_DIR=$GameDir"
    if ($LASTEXITCODE) { throw 'Transport peer build failed.' }
}
& (Join-Path $repo 'tools/package.ps1') | Out-Host
$cecil = Get-ChildItem "$env:USERPROFILE/.nuget/packages/microsoft.codecoverage" -Filter Mono.Cecil.dll -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if (!$cecil) { throw 'Run dotnet restore tests/Core.Tests/Core.Tests.csproj to acquire the Cecil metadata reader.' }
[void][Reflection.Assembly]::LoadFrom($cecil.FullName)
$qaRoot=Join-Path $repo 'artifacts/qa'
$run=Get-CheckedPath $qaRoot (Join-Path $qaRoot ('game-'+[Guid]::NewGuid().ToString('N')))
$copy=Get-CheckedPath $run (Join-Path $run 'GameCopy')
[IO.Directory]::CreateDirectory($copy) | Out-Null
Write-Output "Run directory: $run"
$exe=Join-Path $copy OxygenNotIncluded.exe
$processes=@()
$scopedEnv=@('QY_NATIVE_RECORDING','QY_SMOKE_ROOT','QY_SMOKE_ROLE','QY_SMOKE_REPORT','QY_SMOKE_SAVE','QY_SMOKE_ENDPOINT','QY_SMOKE_FINGERPRINT','QY_SMOKE_PERSISTENCE','QY_SMOKE_STEAM_LOBBY','QY_SMOKE_SOAK','QY_SMOKE_CAPTURE','QY_SMOKE_HOST_CAPTURE','ONI_COOP_BIND','SteamAppId','SteamGameId')
$previousEnv=@{}
foreach ($name in $scopedEnv) { $previousEnv[$name]=[Environment]::GetEnvironmentVariable($name) }
try {
    & robocopy $GameDir $copy /E /XJ /R:1 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Game copy failed (robocopy $LASTEXITCODE)." }
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
    if ($SoloPerformance) {
        & (Join-Path $PSScriptRoot '../Performance/measure-solo.ps1') -RunRoot $run -GameCopy $copy -SavePath $SavePath -TimeoutSeconds $TimeoutSeconds
        return
    }
    $gamePackage=Join-Path $repo 'artifacts/QueyangCoop-dev.zip'
    $fixture=Join-Path $PSScriptRoot 'bin/Release/net48/SmokeDriver.dll'
    $selector=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $selector.Start()
    $endpoint=$selector.LocalEndpoint.ToString()
    $selector.Stop()
    $env:SteamAppId='457140'
    $env:SteamGameId='457140'
    $env:ONI_COOP_BIND=$endpoint
    $env:QY_SMOKE_ENDPOINT=if ($NativeEndpoint) {$NativeEndpoint} else {$endpoint}
    $env:QY_SMOKE_SOAK=if($SoakSeconds){'1'}else{'0'}
    $env:QY_SMOKE_STEAM_LOBBY=if($SteamLobby){'1'}else{'0'}
    $env:QY_SMOKE_PERSISTENCE=if($Persistence){'1'}else{'0'}
    $env:QY_SMOKE_CAPTURE=if($LifecycleScreenshot){'1'}else{'0'}
    $env:QY_SMOKE_HOST_CAPTURE=if($SecondGame){'1'}else{'0'}
    if ($NativeRecord) { $env:QY_NATIVE_RECORDING=Join-Path $run 'native-recording'; [IO.Directory]::CreateDirectory($env:QY_NATIVE_RECORDING) | Out-Null }
    if ($NativeReplay) { $env:QY_NATIVE_RECORDING=Get-CheckedPath $qaRoot (Resolve-Path -LiteralPath $NativeReplay).Path }
    $roles=if ($NativeReplay -or $NativeEndpoint) { 'guest-native' } elseif ($Lifecycle) { 'guest-lifecycle' } elseif ($GuestOnly) { 'guest-readonly' } elseif ($SteamLobby) { 'host' } else { @('host','client') }
    foreach ($role in $roles) {
        $klei=Join-Path $run "$role/Klei"
        $gameProfile=Join-Path $klei 'OxygenNotIncluded'
        $mods=Join-Path $gameProfile mods
        $mod=Join-Path $mods 'dev/QueyangCoop'
        [IO.Directory]::CreateDirectory($mod) | Out-Null
        [IO.Compression.ZipFile]::ExtractToDirectory($gamePackage,$mod)
        Copy-Item -LiteralPath $fixture -Destination $mod
        # Dev distribution type and Vanilla DLC id are the values in the game's serialized settings.
        # KMod.Local.Subscribe stores the Unity Mono hash of the directory name in label.version.
        # This is the value written by build 744825 for QueyangCoop. A zero here stages a
        # reinstall, disabling this otherwise freshly installed fixture on its next boot.
        $modDb=@{version=1;mods=@(@{label=@{distribution_platform=4;id='QueyangCoop';title='Queyang Co-op (Development)';version=-181413377};status=1;enabled=$true;enabledForDlc=@('');crash_count=0;reinstall_path=$null;staticID=$ModId})}
        [IO.File]::WriteAllText((Join-Path $mods 'mods.json'),($modDb|ConvertTo-Json -Depth 6))
        $prefs="strings:`n  SavesDefaultToCloud: Disabled`nints:`n  EXPANSION1_ID.ENABLED: 0`n  DLC2_ID.ENABLED: 0`n  DLC3_ID.ENABLED: 0`n  DLC4_ID.ENABLED: 0`n  DLC5_ID.ENABLED: 0`n  COSMETIC1_ID.ENABLED: 0`n  LanguageConfirmationVersion: 2`nfloats: {}`n"
        [IO.File]::WriteAllText((Join-Path $gameProfile 'kplayerprefs.yaml'),$prefs)
        $env:QY_SMOKE_ROOT=$klei
        $env:QY_SMOKE_ROLE=$role
        $env:QY_SMOKE_REPORT=Join-Path $run "$role.report"
        $env:QY_SMOKE_SAVE=Join-Path $gameProfile 'save_files/Fixture/Fixture.sav'
        if ($role -eq 'host' -or $Lifecycle) {
            [IO.Directory]::CreateDirectory((Split-Path $env:QY_SMOKE_SAVE)) | Out-Null
            Copy-Item -LiteralPath $SavePath -Destination $env:QY_SMOKE_SAVE
        }
        $log=Join-Path $run "$role.log"
        $peerExe=Join-Path $PSScriptRoot 'Peer/bin/Release/net48/Peer.exe'
        if (($role -eq 'client' -and !$SecondGame) -or $guestFixture) {
            $ids=foreach($modulePath in @((Join-Path $managed 'Assembly-CSharp.dll'),(Join-Path $mod 'QueyangCoop.dll'),(Join-Path $mod 'Queyang.Core.dll'))) {
                $image=[Mono.Cecil.ModuleDefinition]::ReadModule($modulePath)
                try { $image.Mvid.ToString() } finally { $image.Dispose() }
            }
            $env:QY_SMOKE_FINGERPRINT=($ids -join '/')
        }
        if ($guestFixture) {
            $env:QY_SMOKE_REPORT=Join-Path $run 'fixture.report'
            $serveArgs=if ($NativeReplay) { @('--native-replay') } else { @('--serve-readonly',([string]($TimeoutSeconds + 30))) }
            $readyMark=if ($NativeReplay) { '^NATIVE_REPLAY_READY' } else { '^READONLY_FIXTURE_READY' }
            $server=Start-Process -FilePath $peerExe -ArgumentList $serveArgs -WorkingDirectory $run -PassThru -WindowStyle Hidden
            $processes+=$server
            $timer=[Diagnostics.Stopwatch]::StartNew()
            while ($timer.Elapsed.TotalSeconds -lt 10 -and !$server.HasExited -and !(Test-Path -LiteralPath $env:QY_SMOKE_REPORT)) {
                Start-Sleep -Milliseconds 100
            }
            if ($server.HasExited -or !(Select-String -LiteralPath $env:QY_SMOKE_REPORT -Pattern $readyMark -Quiet)) {
                throw 'Cannot start the synthetic test fixture host.'
            }
            $env:QY_SMOKE_REPORT=Join-Path $run "$role.report"
        }
        if ($role -eq 'client' -and !$SecondGame) {
            $peerOptions=@{}
            if ($NativeRecord) { $peerOptions.ArgumentList=@('--native-record') }; if ($SoakSeconds -gt 0) { $peerOptions.ArgumentList=@('--soak',"$SoakSeconds") }
            $process=Start-Process -FilePath $peerExe -WorkingDirectory $run -PassThru -WindowStyle Hidden @peerOptions
        } else {
            $displayArgs=if ($LifecycleScreenshot -or $NativeReplay -or $NativeEndpoint -or $SecondGame -or ($NativeRecord -and $role -eq 'host')) { @('-screen-width','1280','-screen-height','800') } else { @('-batchmode') }
            $playerArgs=@($displayArgs) + @('-screen-fullscreen','0','-logFile',('"' + $log + '"'))
            $process=Start-Process -FilePath $exe -WorkingDirectory $copy -ArgumentList $playerArgs -PassThru -WindowStyle Hidden
        }
        $processes += $process
        $timer=[Diagnostics.Stopwatch]::StartNew()
        $sampleAt=0
        $savedManifest=$false
        $restarted=$false
        $done=if($role -eq 'host' -and !$SteamLobby) { '^HOST_READY' } else { '^PASS ' }
        while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            if (Test-Path -LiteralPath $env:QY_SMOKE_REPORT) {
                $lines=Get-Content -LiteralPath $env:QY_SMOKE_REPORT
                if ($lines -match '^FAIL ') { throw "Game smoke $role failed. See report in $run" }
                if (($role -in @('guest-native','client') -or (($NativeRecord -or $SecondGame) -and $role -eq 'host')) -and $lines -match '^NATIVE_RESTART_REQUIRED') {
                    if ($restarted) { throw 'The fresh isolated profile requested a second restart.' }
                    if (!$process.WaitForExit(15000)) { throw 'Native restart requested but game did not quit.' }
                    $restarted=$true
                    # A freshly installed dev mod can be disabled until the player explicitly
                    # enables it. Preserve the ONI-generated version and all other mod entries.
                    $isolatedDb=Join-Path $mods 'mods.json'
                    $settings=Get-Content -LiteralPath $isolatedDb -Raw -Encoding UTF8 | ConvertFrom-Json
                    $entry=@($settings.mods | Where-Object { $_.label.id -ceq 'QueyangCoop' -and $_.label.distribution_platform -eq 4 })
                    if ($entry.Count -ne 1) { throw 'Missing isolated development mod entry after restart.' }
                    $entry[0].enabled=$true
                    $entry[0].enabledForDlc=@('')
                    [IO.File]::WriteAllText($isolatedDb,($settings | ConvertTo-Json -Depth 12))
                    $firstLaunch=if($SecondGame){"$role-first-launch"}else{'first-launch'}
                    Move-Item -LiteralPath $env:QY_SMOKE_REPORT -Destination (Join-Path $run "$firstLaunch.report")
                    Move-Item -LiteralPath $log -Destination (Join-Path $run "$firstLaunch.log")
                    $process=Start-Process -FilePath $exe -WorkingDirectory $copy -ArgumentList $playerArgs -PassThru -WindowStyle Hidden
                    $processes[$processes.Count-1]=$process
                    continue
                }
                if ($SoakSeconds -and $role -eq 'client' -and $lines -match '^SOAK three concurrent guests Live') {
                    if (!$savedManifest) {
                        & (Join-Path $PSScriptRoot '../Performance/record-soak-input.ps1') -SoakRun $run | Out-Null
                        $savedManifest=$true
                    }
                    if ($timer.Elapsed.TotalSeconds -ge $sampleAt) {
                        $hostProcess=$processes[0]
                        $hostProcess.Refresh()
                        $sample=[pscustomobject]@{
                            seconds=[int]([DateTime]::Now-$hostProcess.StartTime).TotalSeconds
                            workingSetBytes=$hostProcess.WorkingSet64
                            privateBytes=$hostProcess.PrivateMemorySize64
                            cpuSeconds=[int]$hostProcess.TotalProcessorTime.TotalSeconds
                        }
                        $sample | Export-Csv -LiteralPath (Join-Path $run 'host-memory.csv') -Append -NoTypeInformation
                        $sampleAt=$timer.Elapsed.TotalSeconds+50
                    }
                }
                if ($lines -match $done) { break }
            }
            if ($process.HasExited) { break }
            Start-Sleep -Milliseconds 500
        }
        if (!(Test-Path -LiteralPath $env:QY_SMOKE_REPORT) -or !(Select-String -LiteralPath $env:QY_SMOKE_REPORT -Pattern $done -Quiet)) {
            throw "Game smoke $role did not reach $done. See $log"
        }
        Get-Content -LiteralPath $env:QY_SMOKE_REPORT
        if ($Lifecycle) {
            $layers=@(Select-String -LiteralPath $log -Pattern '\[QueyangCoop\] guest-closed simStep=0 findChore=0 setChore=0 saveAttempts=0 worldStarts=0 views=[1-9]\d*')
            $rounds=@(Select-String -LiteralPath $env:QY_SMOKE_REPORT -Pattern '^LIFECYCLE_ROUND_PASS round=[123]$')
            $fixtureLog=Join-Path $run 'fixture.report'
            $joins=@(Select-String -LiteralPath $fixtureLog -Pattern '^READONLY_FIXTURE_CONNECTED \d+$' | ForEach-Object Line | Sort-Object -Unique)
            $leaves=@(Select-String -LiteralPath $fixtureLog -Pattern '^READONLY_FIXTURE_DISCONNECTED \d+$' | ForEach-Object Line | Sort-Object -Unique)
            $inspects=@(Select-String -LiteralPath $fixtureLog -Pattern '^READONLY_FIXTURE_COMMAND inspect \d+,\d+$')
            if ($layers.Count -ne 3 -or $rounds.Count -ne 3 -or $joins.Count -ne 3 -or $leaves.Count -ne 3 -or $inspects.Count -ne 3) {
                throw "Incomplete lifecycle evidence: rounds=$($rounds.Count), counts=$($layers.Count), joins=$($joins.Count), leaves=$($leaves.Count), inspections=$($inspects.Count)"
            }
            if ($LifecycleScreenshot) {
                $image=Join-Path $run 'guest-lifecycle.png'
                if (Test-Path -LiteralPath $image) {
                    Add-Type -AssemblyName System.Drawing
                    $frame=[Drawing.Bitmap]::FromFile($image)
                    try {
                        $colors=[Collections.Generic.HashSet[int]]::new()
                        for ($y=3;$y -lt $frame.Height -and $colors.Count -lt 8;$y+=11) {
                            for ($x=3;$x -lt $frame.Width -and $colors.Count -lt 8;$x+=11) {
                                [void]$colors.Add($frame.GetPixel($x,$y).ToArgb())
                            }
                        }
                        if ($frame.Width -eq 1280 -and $frame.Height -eq 800 -and $colors.Count -ge 8) {
                            Write-Output "1280x800 Unity framebuffer candidate for visual inspection: $image"
                        } else {
                            Write-Output "Inconclusive framebuffer: $($frame.Width)x$($frame.Height), sampled colors=$($colors.Count). Visual verification remains pending."
                        }
                    } finally { $frame.Dispose() }
                }
                else { Write-Output 'Unity did not produce a framebuffer image in this mode. Visual inspection remains pending.' }
            }
        }
    }
} finally {
    $hostReport=Join-Path $run 'host.report'
    $featureFailed=$false
    if ($processes.Count -gt 0 -and !$processes[0].HasExited -and (Test-Path -LiteralPath $hostReport) -and
        (Select-String -LiteralPath $hostReport -Pattern '^HOST_READY' -Quiet)) {
        [IO.File]::WriteAllText((Join-Path $run 'features.flag'),'run')
        $featureWait=[Diagnostics.Stopwatch]::StartNew()
        while ($featureWait.Elapsed.TotalSeconds -lt 12 -and !(Select-String -LiteralPath $hostReport -Pattern '^FEATURE_CHECKS_DONE' -Quiet)) {
            Start-Sleep -Milliseconds 250
        }
        Get-Content -LiteralPath $hostReport
        $featureFailed=!(Select-String -LiteralPath $hostReport -Pattern '^FEATURE_CHECKS_DONE' -Quiet)
    }
    foreach ($p in $processes) {
        if (!$p.HasExited) {
            if ($p.Path -ine $exe -and $p.Path -ine $peerExe) { throw 'Refusing to close unrelated process.' }
            Stop-Process -Id $p.Id -Force
            $p.WaitForExit(10000) | Out-Null
        }
    }
    $persistenceError=$null
    if ($Persistence) {
        try {
            if (!(Test-Path -LiteralPath $hostReport) -or !(Select-String -LiteralPath $hostReport -Pattern '^PERSIST SAVED ' -Quiet)) {
                throw 'No disposable host save was produced for persistence test.'
            }
            & (Join-Path $PSScriptRoot 'persistence.ps1') -RunRoot $run -GameCopy $copy -TimeoutSeconds $TimeoutSeconds
        } catch { $persistenceError=$_ }
    }
    foreach ($name in $scopedEnv) { [Environment]::SetEnvironmentVariable($name,$previousEnv[$name]) }
    if (($initialSaveHash -and (Get-FileHash -LiteralPath $SavePath).Hash -ne $initialSaveHash) -or
        (Get-FileHash -LiteralPath $sourceDll).Hash -ne $initialDllHash -or
        ($initialDbHash -and (Get-FileHash -LiteralPath $realDb).Hash -ne $initialDbHash)) { throw 'Unexpected change to original test inputs.' }
    # Keep reports and raw, private engine logs locally; remove the copied game and save files.
    foreach ($child in 'GameCopy','host','client','guest-readonly','guest-lifecycle','guest-native') {
        $owned=Get-CheckedPath $run (Join-Path $run $child)
        if (Test-Path -LiteralPath $owned) { Remove-Item -LiteralPath $owned -Recurse -Force }
    }
    Write-Output "Original input hashes checked. Reports and local logs: $run"
    if ($persistenceError) { throw $persistenceError }
    if ($featureFailed) { throw "Host feature checks failed or timed out. See $hostReport" }
}

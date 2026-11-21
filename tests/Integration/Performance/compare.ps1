[CmdletBinding()]
param([Parameter(Mandatory)][string]$SoakRun,[Parameter(Mandatory)][string]$SoloReport,
      [Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
$culture=[Globalization.CultureInfo]::InvariantCulture
function Number([string]$s) { [double]::Parse($s.Replace(',','.'),$culture) }
function Measure-TenMinutes([string]$path,[string]$prefix) {
    $pattern='^'+$prefix+'_HOST uptime=([0-9.,]+) simTime=([0-9.,]+) speed=(\d+)'
    $rows=@(foreach($line in Get-Content -LiteralPath $path) {
        if($line -match $pattern) { [pscustomobject]@{wall=(Number $matches[1]);sim=(Number $matches[2]);speed=[int]$matches[3]} }
    })
    if ($rows.Length -lt 12) { throw "$prefix requires 12 samples: initial, warm-up, 10 measured minutes" }
    foreach($i in 1..11) {
        $dt=$rows[$i].wall - $rows[$i-1].wall
        if ($dt -lt 59 -or $dt -gt 90 -or $rows[$i].sim -le $rows[$i-1].sim) {
            throw "$prefix discontinuity between samples $($i-1) and $i"
        }
    }
    foreach($sample in $rows[0..11]) {
        if ($sample.speed -ne 0) { throw "$prefix has a non-1x speed setting" }
    }
    $wall=$rows[11].wall-$rows[1].wall
    $sim=$rows[11].sim-$rows[1].sim
    [pscustomobject]@{initialClock=$rows[0].sim;wallSeconds=$wall;simulatedSeconds=$sim;
        simulatedSecondsPerWallSecond=$sim/$wall;sampledIntervals=10}
}
$hostReport=Join-Path $SoakRun 'host.report'
$clientLines=Get-Content -LiteralPath (Join-Path $SoakRun 'client.report')
if (!($clientLines -match '^SOAK three concurrent guests Live') -or !($clientLines -match '^PASS real host, three TCP guests') -or
    !(Select-String -LiteralPath $SoloReport -Pattern '^PASS solo' -Quiet)) { throw 'Need successful connected and solo runs.' }
$peerSeconds=@(foreach($line in $clientLines){if($line -match '^SOAK seconds=(\d+)') { [int]$matches[1] }})
if ($peerSeconds.Length -lt 11 -or ($peerSeconds|Measure-Object -Maximum).Maximum -lt 650) { throw 'Missing connected guest progress during the measurement.' }
$solo=Measure-TenMinutes $SoloReport 'SOLO'
$coop=Measure-TenMinutes $hostReport 'SOAK'
if ([Math]::Abs($solo.initialClock-$coop.initialClock) -gt 2) { throw 'Initial simulation clocks differ by >2 seconds.' }
$metricRows=@(foreach($line in Get-Content -LiteralPath (Join-Path $SoakRun 'host.log')) {
    if($line -match '\[QueyangCoop\] role=host seconds=([\d.]+) pumpP95ms=([\d.]+).* txBps=([\d.]+) rxBps=([\d.]+)') {
        [pscustomobject]@{seconds=(Number $matches[1]);pumpP95ms=(Number $matches[2]);txBps=(Number $matches[3]);rxBps=(Number $matches[4])}
    }
})
if ($metricRows.Length -lt 11) { throw 'Need 11 one-minute host pump reports.' }
$metrics=$metricRows[1..10]
if (@($metrics|Where-Object {$_.seconds -lt 59 -or $_.seconds -gt 90}).Count) { throw 'Unexpected host metric interval.' }
$slowdown=100*(1-$coop.simulatedSecondsPerWallSecond/$solo.simulatedSecondsPerWallSecond)
$maxP95=($metrics|Measure-Object pumpP95ms -Maximum).Maximum
$fullRunP95=($metricRows|Measure-Object pumpP95ms -Maximum).Maximum
$completed=$clientLines -match '^PASS real host, three TCP guests, (\d+) seconds,'
if (@($completed).Count -ne 1 -or $completed[0] -notmatch 'guests, (\d+) seconds,') {
    throw 'Missing the completed run duration.'
}
$duration=[int]$matches[1]
if ($duration -le 0) { throw 'Invalid connected run duration.' }
$rates=@(foreach($line in $clientLines) {
    if ($line -match '^SOAK peer=([01]) .* rxBytes=(\d+) ') {
        [pscustomobject]@{peer=[int]$matches[1]; averageBytesPerSecond=[long]$matches[2]/$duration}
    } elseif ($line -match '^SOAK reconnectTotal rxBytes=(\d+) ') {
        [pscustomobject]@{peer=2; averageBytesPerSecond=[long]$matches[1]/$duration}
    }
})
if (@($rates.peer|Sort-Object -Unique).Count -ne 3 -or $rates.Count -ne 3) { throw 'Incomplete per-peer byte counts.' }
$manifest=Get-Content -LiteralPath (Join-Path $SoakRun 'soak-input.json') -Raw|ConvertFrom-Json
$result=[ordered]@{
    source=$manifest; connectedRun=(Split-Path $SoakRun -Leaf);
    method='Same input, fixed 1x, 60 s warm-up and ten 60 s intervals. Simulation clock rate is a proxy for simulation speed.';
    solo=$solo; connected=$coop; slowdownPercent=$slowdown; maximumMinutePumpP95ms=$maxP95;
    minuteHostMetrics=$metrics; fullRunMinutePumpP95Max=$fullRunP95; peerDownstream=$rates;
    throughputPass=($slowdown -le 15.0); pumpPass=($fullRunP95 -le 4.0);
    downstreamPass=(@($rates|Where-Object {$_.averageBytesPerSecond -gt 2MB}).Count -eq 0)
}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),($result|ConvertTo-Json -Depth 5))
"Performance evidence: $([IO.Path]::GetFullPath($OutputPath))"
"Simulation slowdown: $([math]::Round($slowdown,3))%; largest minute pump p95 over the connected run: $fullRunP95 ms"
if (!$result.throughputPass -or !$result.pumpPass -or !$result.downstreamPass) { throw 'Performance budget exceeded; see comparison.json for measurements.' }

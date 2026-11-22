# Capture the two Unity windows by HWND using Windows Graphics Capture. A window
# covered by the other game keeps producing its own composed frames.
if (-not ('QyWindowCapture' -as [type])) {
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class QyWindowCapture {
    delegate bool Visit(IntPtr window, IntPtr arg);
    [DllImport("user32.dll")] static extern bool EnumWindows(Visit visitor,IntPtr arg);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder name,int length);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window,int command);
    public static long GameWindow(uint pid) {
        IntPtr selected=IntPtr.Zero;
        var observed=new StringBuilder();
        EnumWindows(delegate(IntPtr h,IntPtr arg) {
            uint candidate; GetWindowThreadProcessId(h,out candidate);
            if(candidate!=pid) return true;
            var name=new StringBuilder(256); GetClassName(h,name,name.Capacity);
            observed.Append(name).Append(" ");
            if(name.ToString().IndexOf("UnityWndClass",StringComparison.OrdinalIgnoreCase)<0) return true;
            selected=h; return false;
        },IntPtr.Zero);
        if(selected==IntPtr.Zero) throw new InvalidOperationException("Unity window missing for test process "+pid+" (classes: "+observed+")");
        if(IsIconic(selected) || !IsWindowVisible(selected)) ShowWindowAsync(selected,4);
        return selected.ToInt64();
    }
}
"@
}
function Start-DualRecording([object]$driver,[string]$name='dupe-motion',[int]$seconds=24) {
    $game=Get-Player $driver
    if (!$game) { throw "No verified $($driver.role) game process for capture." }
    $window=[QyWindowCapture]::GameWindow([uint32]$game.Id)
    $out=Join-Path $driver.base "$name.mp4"
    $log=Join-Path $driver.base $(if($name -eq 'dupe-motion'){'capture.log'}else{"$name-capture.log"})
    # Background windows can deliver fewer graphics updates than the nominal source rate.
    # Use elapsed capture time for frame presentation, including gaps between updates.
    $graph="gfxcapture=hwnd=${window}:width=1280:height=720:resize_mode=scale:max_framerate=30:capture_cursor=0:display_border=0,hwdownload,format=bgra,settb=AVTB,setpts=(RTCTIME-RTCSTART)/1000000/TB,fps=30,format=yuv420p[v]"
    $opts=@('-hide_banner','-nostdin','-loglevel','error','-filter_complex',$graph,'-map','[v]','-an','-c:v','libx264','-preset','veryfast','-threads','2','-crf','19','-r','30','-fps_mode','cfr','-t',"$seconds",'-movflags','+faststart',('"' + $out + '"'))
    $p=Start-Process -FilePath (Get-Command ffmpeg).Source -ArgumentList $opts -WindowStyle Hidden -PassThru -RedirectStandardError $log
    [ordered]@{utc=[DateTime]::UtcNow.ToString('o');qpc=[Diagnostics.Stopwatch]::GetTimestamp();qpcHz=[Diagnostics.Stopwatch]::Frequency;gameProcess=$game.Id;window=$window} |
        ConvertTo-Json |Set-Content (Join-Path $driver.base $(if($name -eq 'dupe-motion'){'capture-clock.json'}else{"$name-clock.json"}))
    $p
}
function Confirm-DualRecording([object]$driver,[Diagnostics.Process]$recorder,[string]$name='dupe-motion',[int]$seconds=24) {
    if (!$recorder -or !$recorder.WaitForExit(30000) -or ($null -ne $recorder.ExitCode -and $recorder.ExitCode -ne 0)) {
        throw "Windows Graphics Capture failed for $($driver.role); see its capture.log"
    }
    $path=Join-Path $driver.base "$name.mp4"
    $json=& ffprobe -v error -select_streams v:0 -show_entries 'stream=codec_name,width,height,avg_frame_rate,nb_frames,duration' -of json $path
    if ($LASTEXITCODE) { throw "Unreadable video: $path" }
    $video=($json | ConvertFrom-Json).streams[0]
    if (!$video -or $video.codec_name -ne 'h264' -or $video.width -ne 1280 -or $video.height -ne 720 -or $video.avg_frame_rate -ne '30/1' -or [int]$video.nb_frames -lt ($seconds*30-10) -or [double]$video.duration -lt ($seconds-.1)) { throw "Bad capture stream: $path" }
    $filter='fps=1,crop=iw*.65:ih*.65,scale=160:90'
    $samples=& ffmpeg -v error -i $path -vf $filter -an -f framemd5 -
    $hashes=@($samples -match '^0,' | ForEach-Object {($_ -split ',')[-1].Trim()} | Sort-Object -Unique)
    $stats=& ffmpeg -v error -i $path -vf "$filter,signalstats,metadata=mode=print:key=lavfi.signalstats.YAVG:file=-" -an -f null NUL
    $means=@($stats -match '^lavfi.signalstats.YAVG=' | ForEach-Object {[double]::Parse(($_ -split '=')[1],[cultureinfo]::InvariantCulture)})
    if ($means.Count -ne $seconds -or ($means | Measure-Object -Minimum).Minimum -le 18 -or $hashes.Count -lt 5) {
        throw "Motion clip contains black or static colony samples: $path"
    }
    $video | Add-Member -NotePropertyName distinctOneSecondSceneHashes -NotePropertyValue $hashes.Count
    $video | Add-Member -NotePropertyName minimumSceneLuma -NotePropertyValue (($means | Measure-Object -Minimum).Minimum)
    $video | ConvertTo-Json |Set-Content (Join-Path $driver.base $(if($name -eq 'dupe-motion'){'capture-stream.json'}else{"$name-stream.json"}))
}

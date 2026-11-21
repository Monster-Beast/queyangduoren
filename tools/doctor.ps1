param([string]$GameDir = $env:ONI_GAME_DIR)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
Push-Location -LiteralPath $repo
try {
    $sdk = & dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'The SDK selected by global.json is unavailable.' }
} finally { Pop-Location }
Write-Output "Selected .NET SDK: $sdk"
Write-Output 'Game target: .NET Framework 4.8. Portable tests: dotnet test tests/Core.Tests/Core.Tests.csproj -c Release'
if (!$GameDir) {
    $steamRoot = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if ($steamRoot) {
        $libraries = @($steamRoot)
        $librariesFile = Join-Path $steamRoot 'steamapps/libraryfolders.vdf'
        if (Test-Path -LiteralPath $librariesFile) {
            $libraries += Select-String -LiteralPath $librariesFile -Pattern '^\s*"path"\s+"([^"]+)"' |
                ForEach-Object { $_.Matches[0].Groups[1].Value.Replace('\\','\') }
        }
        foreach ($library in ($libraries | Select-Object -Unique)) {
            $candidate = Join-Path $library 'steamapps/common/OxygenNotIncluded'
            if (Test-Path -LiteralPath "$candidate/OxygenNotIncluded_Data/Managed/Assembly-CSharp.dll") {
                $GameDir = $candidate
                break
            }
        }
    }
}
if (!$GameDir) { throw 'Game installation missing. Set ONI_GAME_DIR to the game folder in the actual Steam Library.' }
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
Write-Output "Game directory: $GameDir"
$manifest = Join-Path (Split-Path (Split-Path $GameDir)) 'appmanifest_457140.acf'
if (Test-Path -LiteralPath $manifest) {
    Select-String -LiteralPath $manifest -Pattern '^\s*"buildid"\s+"(\d+)"' |
        ForEach-Object { Write-Output "Steam depot build: $($_.Matches[0].Groups[1].Value)" }
}
$managed = Join-Path $GameDir 'OxygenNotIncluded_Data/Managed'
[xml]$project = Get-Content -LiteralPath (Join-Path $repo 'src/Game/Game.csproj') -Raw
$references = $project.Project.ItemGroup.GameAssembly.Include | Where-Object { $_ }
if (!$references) { throw 'Cannot read required game DLL list from Game.csproj.' }
foreach ($name in ($references -split ';')) {
    $file = Join-Path $managed ($name + '.dll')
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing required game reference: $file" }
    $assembly = [Reflection.AssemblyName]::GetAssemblyName($file)
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    Write-Output "$name  $($assembly.Version)  SHA256 $hash"
}
$log = Join-Path $env:USERPROFILE 'AppData/LocalLow/klei/Oxygen Not Included/Player.log'
if (Test-Path -LiteralPath $log) {
    $build = Select-String -LiteralPath $log -Pattern '\bBuild:\s+(\S+)' | Select-Object -Last 1
    if ($build) { Write-Output "Most recent Player.log build (may be stale): $($build.Matches[0].Groups[1].Value)" }
}
Write-Output 'Set ONI_GAME_DIR in your shell if the game resides in a secondary Steam library.'

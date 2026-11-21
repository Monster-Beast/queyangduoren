param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'dev-common.ps1')
$repo = Split-Path $PSScriptRoot
$output = Join-Path $repo "src/Game/bin/$Configuration/net48"
$metadata = Join-Path $repo 'src/Game/metadata'
$artifacts = Join-Path $repo 'artifacts'
$zip = Join-Path $artifacts 'QueyangCoop-dev.zip'
$inputs = @{}
foreach ($name in $PackageFiles) {
    $file = if ($name -like '*.dll') { Join-Path $output $name } else { Join-Path $metadata $name }
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing $file. Build QueyangDuoren.sln -c $Configuration first." }
    if ($name -like '*.dll' -and [Reflection.AssemblyName]::GetAssemblyName($file).Name -cne [IO.Path]::GetFileNameWithoutExtension($name)) {
        throw "Unexpected managed assembly identity: $file"
    }
    $inputs[$name] = $file
}
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
$temp = Join-Path $artifacts ('.QueyangCoop-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    $archive = [IO.Compression.ZipFile]::Open($temp, 'Create')
    try {
        foreach ($name in $PackageFiles) {
            $entry = $archive.CreateEntry($name,'Optimal')
            $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
            $sourceStream = [IO.File]::OpenRead($inputs[$name])
            try {
                $destinationStream = $entry.Open()
                try { $sourceStream.CopyTo($destinationStream) } finally { $destinationStream.Dispose() }
            } finally { $sourceStream.Dispose() }
        }
    } finally { $archive.Dispose() }
    if ([IO.File]::Exists($zip)) { [IO.File]::Replace($temp,$zip,[System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temp,$zip) }
} finally {
    if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
}
Get-FileHash -LiteralPath $zip -Algorithm SHA256

$PackageFiles = @('QueyangCoop.dll','Queyang.Core.dll','mod.yaml','mod_info.yaml')
$ModId = 'MonsterBeast.QueyangCoop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-DefaultModsRoot {
    Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Klei/OxygenNotIncluded/mods'
}

function Get-CheckedPath([string]$root,[string]$path) {
    $root = [IO.Path]::GetFullPath($root).TrimEnd('\','/')
    $full = [IO.Path]::GetFullPath($path)
    if (!$full.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes the intended directory: $full"
    }
    # Inspect existing ancestors, including the target, before any move or recursive cleanup.
    $item = $full
    while ($item) {
        if (Test-Path -LiteralPath $item) {
            if ((Get-Item -Force -LiteralPath $item).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Linked install paths are unsupported: $item"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($item)
        if (!$parent -or $parent -eq $item) { break }
        $item = $parent
    }
    return $full
}

function Test-ModId([string]$directory) {
    $meta = Join-Path $directory 'mod.yaml'
    return (Test-Path -LiteralPath $meta -PathType Leaf) -and
        [bool](Select-String -LiteralPath $meta -Pattern '^\s*staticID\s*:\s*["'']?MonsterBeast\.QueyangCoop["'']?\s*$' -Quiet)
}

function Assert-DevPackage([string]$directory) {
    if (!(Test-ModId $directory)) { throw "Unrecognized mod at $directory" }
    $entries = @(Get-ChildItem -LiteralPath $directory -Force)
    if ($entries.Count -ne $PackageFiles.Count) { throw "Unexpected files in $directory. Preserve this directory before replacing." }
    foreach ($entry in $entries) {
        if ($entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            $PackageFiles -cnotcontains $entry.Name) { throw "Unexpected package entry: $($entry.FullName)" }
        if ($entry.Extension -eq '.dll' -and [Reflection.AssemblyName]::GetAssemblyName($entry.FullName).Name -cne $entry.BaseName) {
            throw "Invalid assembly: $($entry.FullName)"
        }
    }
}

function Get-DevLayout([string]$modsRoot) {
    if (!$modsRoot) { $modsRoot = Get-DefaultModsRoot }
    $modsRoot = [IO.Path]::GetFullPath($modsRoot).TrimEnd('\','/')
    if ([IO.Path]::GetFileName($modsRoot) -ine 'mods') { throw 'Expected the ONI mods folder (ending in mods).' }
    $parent = [IO.Path]::GetDirectoryName($modsRoot)
    $target = Get-CheckedPath $modsRoot (Join-Path $modsRoot 'dev/QueyangCoop')
    $archiveRoot = Get-CheckedPath $parent (Join-Path $parent 'QueyangCoop-backups')
    [pscustomobject]@{ModsRoot=$modsRoot;Target=$target;ArchiveRoot=$archiveRoot}
}

function Assert-NoDuplicate($layout) {
    foreach ($source in @('dev','Local','Steam')) {
        $dir = Join-Path $layout.ModsRoot $source
        if (!(Test-Path -LiteralPath $dir)) { continue }
        foreach ($mod in (Get-ChildItem -Directory -LiteralPath $dir)) {
            if ($mod.FullName -ine $layout.Target -and (Test-ModId $mod.FullName)) {
                throw "Duplicate staticID in $($mod.FullName). Keep only one copy of $ModId."
            }
        }
    }
}

function Open-ModZip([string]$package) {
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $package).Path)
    try {
        if ($archive.Entries.Count -ne $PackageFiles.Count) { throw 'Unexpected number of ZIP entries.' }
        foreach ($name in $PackageFiles) {
            $entry = $archive.GetEntry($name)
            if (!$entry -or $entry.Length -lt 1 -or $entry.Length -gt 16MB) { throw "Missing or invalid ZIP entry: $name" }
        }
        return $archive
    } catch {
        $archive.Dispose()
        throw
    }
}

function Publish-DevPackage($layout,[scriptblock]$prepare) {
    if ([IO.Path]::GetFullPath((Get-DefaultModsRoot)) -ieq $layout.ModsRoot -and (Get-Process OxygenNotIncluded -ErrorAction SilentlyContinue)) {
        throw 'Exit Oxygen Not Included before replacing its loaded mod.'
    }
    $backups = $layout.ArchiveRoot
    [IO.Directory]::CreateDirectory($backups) | Out-Null
    $lockFile = Get-CheckedPath $backups (Join-Path $backups '.dev-install.lock')
    $lock = [IO.File]::Open($lockFile,'OpenOrCreate','ReadWrite','None')
    $stage = $null
    try {
        Assert-NoDuplicate $layout
        $token = [Guid]::NewGuid().ToString('N')
        $stage = Get-CheckedPath $backups (Join-Path $backups ".stage-$token")
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        & $prepare $stage
        Assert-DevPackage $stage
        $target = Get-CheckedPath $layout.ModsRoot $layout.Target
        [IO.Directory]::CreateDirectory((Split-Path $target)) | Out-Null
        $backup = $null
        if (Test-Path -LiteralPath $target) {
            Assert-DevPackage $target
            $changed = $false
            foreach ($name in $PackageFiles) {
                if ((Get-FileHash -LiteralPath (Join-Path $target $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $stage $name)).Hash) { $changed = $true; break }
            }
            if (!$changed) { Write-Output "Already installed: $target"; return }
            $backup = Get-CheckedPath $backups (Join-Path $backups ("backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $token))
            [IO.Directory]::Move($target,$backup)
            Write-Output "Previous version backup: $backup"
        }
        try { [IO.Directory]::Move($stage,$target) }
        catch {
            if ($backup -and !(Test-Path -LiteralPath $target)) { [IO.Directory]::Move($backup,$target) }
            throw
        }
        Write-Output "Installed: $target"
    } finally {
        try {
            if ($stage -and (Test-Path -LiteralPath $stage)) {
                $checked = Get-CheckedPath $backups $stage
                Remove-Item -LiteralPath $checked -Recurse -Force
            }
        } finally { $lock.Dispose() }
    }
}

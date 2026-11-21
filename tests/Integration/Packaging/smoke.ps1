# Run after building the solution and generating the candidate ZIP.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$tools = Join-Path $repo 'tools'
. (Join-Path $tools 'dev-common.ps1')
$qaRoot = Join-Path $repo 'artifacts/qa'
$run = Get-CheckedPath $qaRoot (Join-Path $qaRoot ('package-' + [Guid]::NewGuid().ToString('N')))
$mods = Join-Path $run mods
$layout = Get-DevLayout $mods
$package = Join-Path $repo 'artifacts/QueyangCoop-dev.zip'
[IO.Directory]::CreateDirectory($run) | Out-Null
try {
    & (Join-Path $tools 'install-dev.ps1') -ModsRoot $mods -Package $package -WhatIf
    if (Test-Path -LiteralPath $mods) { throw 'WhatIf wrote to disk.' }
    & (Join-Path $tools 'install-dev.ps1') -ModsRoot $mods -Package $package
    & (Join-Path $tools 'install-dev.ps1') -ModsRoot $mods -Package $package
    if (@(Get-ChildItem -Directory -LiteralPath $layout.ArchiveRoot).Count) { throw 'Identical reinstall created backups.' }
    $meta = Join-Path $layout.Target mod_info.yaml
    Add-Content -LiteralPath $meta -Value '# rollback fixture'
    $before = (Get-FileHash -LiteralPath $meta).Hash
    & (Join-Path $tools 'install-dev.ps1') -ModsRoot $mods -Package $package
    if ($before -eq (Get-FileHash -LiteralPath $meta).Hash) { throw 'Upgrade failed.' }
    $saved = @(Get-ChildItem -Directory -LiteralPath $layout.ArchiveRoot)
    if ($saved.Count -ne 1) { throw 'Expected one retrievable backup.' }
    & (Join-Path $tools 'restore-dev.ps1') -ModsRoot $mods -BackupPath $saved[0].FullName
    if ($before -ne (Get-FileHash -LiteralPath $meta).Hash) { throw 'Rollback content mismatch.' }
    $duplicate = Join-Path $mods 'Local/duplicate'
    [IO.Directory]::CreateDirectory($duplicate) | Out-Null
    Copy-Item -LiteralPath (Join-Path $layout.Target mod.yaml) -Destination $duplicate
    $caught = $false
    try { & (Join-Path $tools 'install-dev.ps1') -ModsRoot $mods -Package $package }
    catch { if ($_.Exception.Message -notlike 'Duplicate staticID*') { throw }; $caught = $true }
    if (!$caught) { throw 'Duplicate mod ID was not detected.' }
    Write-Output 'PASS: package validation, WhatIf, clean install, idempotence, backup, rollback, duplicate detection.'
} finally {
    $checked = Get-CheckedPath $qaRoot $run
    if (Test-Path -LiteralPath $checked) { Remove-Item -LiteralPath $checked -Recurse -Force }
}

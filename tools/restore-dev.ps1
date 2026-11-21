[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string]$BackupPath,[string]$ModsRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'dev-common.ps1')
$layout = Get-DevLayout $ModsRoot
$backup = Get-CheckedPath $layout.ArchiveRoot (Resolve-Path -LiteralPath $BackupPath).Path
if ((Split-Path $backup) -ine $layout.ArchiveRoot -or (Split-Path -Leaf $backup) -notmatch '^backup-\d{8}-\d{6}-[0-9a-f]{32}$') {
    throw 'Choose the exact previous-version backup printed by install-dev.ps1.'
}
Assert-DevPackage $backup
Assert-NoDuplicate $layout
if (!$PSCmdlet.ShouldProcess($layout.Target,"Restore from $backup and preserve the currently installed version")) { return }
Publish-DevPackage $layout {
    param($stage)
    foreach ($name in $PackageFiles) {
        Copy-Item -LiteralPath (Join-Path $backup $name) -Destination (Join-Path $stage $name)
    }
}

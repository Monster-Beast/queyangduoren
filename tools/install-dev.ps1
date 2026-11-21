[CmdletBinding(SupportsShouldProcess)]
param([string]$Package,[string]$ModsRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'dev-common.ps1')
if (!$Package) { $Package = Join-Path (Split-Path $PSScriptRoot) 'artifacts/QueyangCoop-dev.zip' }
$layout = Get-DevLayout $ModsRoot
Assert-NoDuplicate $layout
if (Test-Path -LiteralPath $layout.Target) { Assert-DevPackage $layout.Target }
$archive = Open-ModZip $Package
try {
    if (!$PSCmdlet.ShouldProcess($layout.Target,"Install validated co-op package; preserve changed previous version in $($layout.ArchiveRoot)")) { return }
    Publish-DevPackage $layout {
        param($stage)
        foreach ($name in $PackageFiles) {
            $entry = $archive.GetEntry($name)
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path $stage $name))
        }
    }
} finally { $archive.Dispose() }

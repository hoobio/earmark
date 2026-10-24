#Requires -Version 7.0

<#
.SYNOPSIS
Bundles the per-architecture Earmark .msix files into one .msixbundle inside a .msixupload for the Store.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $MsixDirectory,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'

$makeAppx = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' } |
    Sort-Object { [version]($_.FullName -replace '.*\\(\d+\.\d+\.\d+\.\d+)\\.*', '$1') } -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $makeAppx) { throw 'makeappx.exe was not found in an installed Windows SDK' }

$work = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'bundle-input'
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null
$packages = @(Get-ChildItem -LiteralPath $MsixDirectory -Recurse -Filter *.msix | Where-Object Name -notmatch '-unsigned\.msix$')
if ($packages.Count -lt 1) { throw "No .msix files under $MsixDirectory" }
$packages | Copy-Item -Destination $work

$bundle = Join-Path $OutputDirectory "Earmark-$Version.msixbundle"
& $makeAppx bundle /d $work /p $bundle /bv $Version /o | Write-Verbose
if ($LASTEXITCODE -ne 0) { throw "makeappx bundle failed (exit $LASTEXITCODE)" }

$upload = Join-Path $OutputDirectory "Earmark-$Version-Store.msixupload"
$zip = [IO.Path]::ChangeExtension($upload, '.zip')
Compress-Archive -LiteralPath $bundle -DestinationPath $zip -Force
Move-Item -LiteralPath $zip -Destination $upload -Force

[pscustomobject]@{
    Version    = $Version
    Bundle     = $bundle
    MsixUpload = $upload
}

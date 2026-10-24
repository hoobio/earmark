#Requires -Version 7.0

<#
.SYNOPSIS
Publishes src/Earmark.Desktop self-contained for one architecture, as loose files or a single-file exe.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string] $Platform = 'x64',

    [ValidateSet('Release', 'Prerelease', 'Dev')]
    [string] $Channel = 'Release',

    [string] $DisplayVersion,

    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    [switch] $SingleFile
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repoRoot 'src\Earmark.Desktop\Earmark.Desktop.csproj'
$runtimeId = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }

$publishArgs = @(
    'publish', $projectPath,
    '-c', 'Release',
    '-r', $runtimeId,
    '--self-contained',
    "-p:EarmarkBuildChannel=$Channel",
    '-o', $OutputDirectory
)
if ($SingleFile) {
    $publishArgs += '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true'
}
else {
    $publishArgs += '-p:PublishSingleFile=false'
}
if ($DisplayVersion) { $publishArgs += "-p:InformationalVersion=$DisplayVersion" }

$attempt = 0
do {
    $attempt++
    & dotnet @publishArgs | Out-Host
    if ($LASTEXITCODE -eq 0) { break }
    Write-Warning "dotnet publish failed (exit $LASTEXITCODE), attempt $attempt of 3"
} while ($attempt -lt 3)
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'Earmark.exe'))) { throw 'The publish produced no Earmark.exe' }
# Skia and HarfBuzz ship 100 MB of native symbols that no crash report can use; Earmark's own PDBs stay for stack traces.
Get-ChildItem -LiteralPath $OutputDirectory -Filter 'lib*.pdb' | Remove-Item

#Requires -Version 7.0

<#
.SYNOPSIS
Builds Earmark (src/Earmark.Desktop) as an unsigned MSIX layout and .msix for one architecture.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string] $Platform = 'x64',

    [ValidateSet('Store', 'Prerelease')]
    [string] $Identity = 'Store',

    [ValidatePattern('^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$')]
    [string] $Version,

    [string] $DisplayVersion,

    [ValidateNotNullOrEmpty()]
    [string] $OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'Earmark.DesktopMsix'),

    [switch] $UnsignedTest
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifestSource = Join-Path $repoRoot 'packaging\windows\Package.appxmanifest'
$assetSource = Join-Path $repoRoot 'packaging\windows\Assets'

function Find-SdkTool {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Name)

    $props = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Packages.props') -Raw)
    $buildToolsVersion = ($props.Project.ItemGroup.PackageVersion | Where-Object Include -eq 'Microsoft.Windows.SDK.BuildTools').Version
    $packagesRoot = $env:NUGET_PACKAGES ?? (Join-Path $env:USERPROFILE '.nuget\packages')
    $candidates = @(
        Get-ChildItem -Path (Join-Path $packagesRoot "microsoft.windows.sdk.buildtools\$buildToolsVersion\bin") -Recurse -Filter $Name -ErrorAction SilentlyContinue
        Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter $Name -ErrorAction SilentlyContinue |
            Sort-Object { [version]($_.FullName -replace '.*\\(\d+\.\d+\.\d+\.\d+)\\.*', '$1') } -Descending
    ) | Where-Object { $_.FullName -match '\\x64\\[^\\]+$' }
    $tool = $candidates | Select-Object -First 1
    if (-not $tool) { throw "$Name was found neither in Microsoft.Windows.SDK.BuildTools $buildToolsVersion nor in an installed Windows SDK" }
    Write-Verbose "Using $($tool.FullName)"
    $tool.FullName
}

function Invoke-Tool {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path, [Parameter(Mandatory)] [string[]] $Arguments)

    & $Path @Arguments | Write-Verbose
    if ($LASTEXITCODE -ne 0) { throw "$(Split-Path $Path -Leaf) $($Arguments[0]) failed (exit $LASTEXITCODE)" }
}

$versionText = (Get-Content -LiteralPath (Join-Path $repoRoot 'version.txt') -Raw).Trim()
if ($versionText -notmatch '^(\d+)\.(\d+)\.(\d+)$') { throw "version.txt does not hold a three-part version: '$versionText'" }
if (-not $Version) { $Version = "$($Matches[1]).$($Matches[2]).$([Math]::Min([int]($env:GITHUB_RUN_NUMBER ?? 0), 65535)).0" }
if ($Version -notmatch '\.0$') { throw "A Store package version must end in .0: $Version" }

$makeAppx = Find-SdkTool 'makeappx.exe'
$makePri = Find-SdkTool 'makepri.exe'

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$layout = Join-Path $OutputDirectory 'layout'
$priRoot = Join-Path $OutputDirectory 'pri'
foreach ($dir in $layout, $priRoot) {
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
}
New-Item -ItemType Directory -Path $layout, (Join-Path $priRoot 'Assets') -Force | Out-Null

$channel = if ($Identity -eq 'Store') { 'Release' } else { 'Prerelease' }
& (Join-Path $PSScriptRoot 'Publish-EarmarkDesktop.ps1') -Platform $Platform -Channel $channel -DisplayVersion $DisplayVersion -OutputDirectory $layout

$visualAssets = Get-ChildItem -LiteralPath $assetSource -File -Filter *.png
New-Item -ItemType Directory -Path (Join-Path $layout 'Assets') -Force | Out-Null
$visualAssets | Copy-Item -Destination (Join-Path $layout 'Assets')
$visualAssets | Copy-Item -Destination (Join-Path $priRoot 'Assets')

$manifest = [IO.File]::ReadAllText($manifestSource)
$stamped = $manifest `
    -replace '(?<=<Identity\s[^>]*)Version="[\d.]+"', "Version=`"$Version`"" `
    -replace '(?<=<Identity\s[^>]*)ProcessorArchitecture="[^"]*"', "ProcessorArchitecture=`"$($Platform.ToLowerInvariant())`""
if ($Identity -eq 'Prerelease') {
    $stamped = $stamped `
        -replace '(?<=<Identity\s[^>]*)Name="Hoobi\.Earmark"', 'Name="Hoobi.Earmark.Prerelease"' `
        -replace '(?<=<(?:uap:)?DisplayName>)Earmark(?=</)', 'Earmark (Prerelease)' `
        -replace '(?<=<uap:VisualElements\s[^>]*)DisplayName="Earmark"', 'DisplayName="Earmark (Prerelease)"'
    if ($stamped -notmatch 'Name="Hoobi\.Earmark\.Prerelease"') { throw 'The Prerelease identity stamp made no change; the identity markers in Package.appxmanifest moved' }
}
if ($stamped -notmatch 'CN=D74C026B-1081-4787-BDE6-0CFA2F1EDD71') { throw 'The Publisher must stay the reserved Store CN' }
$manifestPath = Join-Path $layout 'AppxManifest.xml'
[IO.File]::WriteAllText($manifestPath, $stamped, [Text.UTF8Encoding]::new($false))

$priConfig = Join-Path $OutputDirectory 'priconfig.xml'
Invoke-Tool $makePri @('createconfig', '/cf', $priConfig, '/dq', 'en-US', '/pv', '10.0.0', '/o')
$priXml = [xml](Get-Content -LiteralPath $priConfig -Raw)
$priXml.SelectNodes('//packaging') | ForEach-Object { $_.ParentNode.RemoveChild($_) | Out-Null }
$priXml.Save($priConfig)
Invoke-Tool $makePri @('new', '/pr', $priRoot, '/cf', $priConfig, '/mn', $manifestPath, '/of', (Join-Path $layout 'resources.pri'), '/o')

$msix = Join-Path $OutputDirectory "Earmark-$versionText-$Platform.msix"
Invoke-Tool $makeAppx @('pack', '/d', $layout, '/p', $msix, '/o')

$unsigned = $null
if ($UnsignedTest) {
    $unsignedNamespaceOid = 'OID.2.25.311729368913984317654407730594956997722=1'
    $unsignedManifest = $stamped -replace '(?<=<Identity\s[^>]*Publisher="[^"]*)(?=")', ", $unsignedNamespaceOid"
    if ($unsignedManifest -notmatch [regex]::Escape($unsignedNamespaceOid)) { throw 'Failed to inject the unsigned-namespace OID into the manifest' }
    [IO.File]::WriteAllText($manifestPath, $unsignedManifest, [Text.UTF8Encoding]::new($false))
    $unsigned = Join-Path $OutputDirectory "Earmark-$versionText-$Platform-unsigned.msix"
    Invoke-Tool $makeAppx @('pack', '/d', $layout, '/p', $unsigned, '/o')
}

[pscustomobject]@{
    Identity       = $Identity
    Platform       = $Platform
    Version        = $Version
    Layout         = $layout
    Msix           = $msix
    MsixUnsignedTest = $unsigned
}

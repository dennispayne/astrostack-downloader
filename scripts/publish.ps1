<#
.SYNOPSIS
    Publishes wgfetch as a portable, self-contained win-x64 NativeAOT directory and zips it.

.DESCRIPTION
    Produces artifacts/publish/win-x64/ (wgfetch.exe plus any native side-by-side DLLs, such as the
    ONNX Runtime libraries once those backends are wired — see docs/REQUIREMENTS.md "Known conflicts"
    §2) and artifacts/wgfetch-<version>-win-x64.zip. The ZIP is what the optional winget
    manifest for wgfetch itself installs (InstallerType: zip, NestedInstallerType: portable).
    See docs/REQUIREMENTS.md, "Deliverables".
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $RuntimeIdentifier = 'win-x64',
    [string] $ArtifactsDirectory = (Join-Path $PSScriptRoot '..' 'artifacts')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $repoRoot 'src' 'WgFetch.Cli' 'WgFetch.Cli.csproj'
$publishDirectory = Join-Path $ArtifactsDirectory 'publish' $RuntimeIdentifier

if (Test-Path $publishDirectory) {
    Remove-Item $publishDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

Write-Host "Publishing $project ($Configuration/$RuntimeIdentifier, NativeAOT)..."
dotnet publish $project `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    -p:PublishAot=true `
    --output $publishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$executable = Join-Path $publishDirectory 'wgfetch.exe'
if (-not (Test-Path $executable)) {
    throw "Expected $executable to exist after publish."
}

# Native side-by-side libraries (the ONNX Runtime DLLs, once those backends are wired) are loaded by
# filename at runtime and must ship beside the EXE. A fully static NativeAOT publish emits none at all,
# so force an array: under Set-StrictMode a bare $null result has no .Count and would fail the build.
$nativeLibraries = @(Get-ChildItem -Path $publishDirectory -Filter '*.dll' -File)
Write-Host "Published $($nativeLibraries.Count) native library file(s) alongside wgfetch.exe."

Copy-Item (Join-Path $repoRoot 'LICENSE') $publishDirectory -Force
Copy-Item (Join-Path $repoRoot 'README.md') $publishDirectory -Force

$version = (& $executable --version 2>$null | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = '0.0.0'
}

$zipPath = Join-Path $ArtifactsDirectory "wgfetch-$version-$RuntimeIdentifier.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath
Write-Host "Wrote $zipPath"

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
Write-Host "SHA256: $hash"

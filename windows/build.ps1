# Builds dist/windows/ClaudeUsageBar.exe (self-contained, single file, no .NET install needed)
# and dist/ClaudeUsageBar-windows-x64.zip.
# Usage: pwsh windows/build.ps1 [-Version 1.2.3] [-Runtime win-x64]
param([string]$Version = '0.0.0-dev', [string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'dist/windows'
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue

dotnet publish (Join-Path $PSScriptRoot 'ClaudeUsageBar') -c Release -r $Runtime --self-contained `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:Version=$Version -o $out
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$arch = $Runtime -replace '^win-', ''
$zip = Join-Path $root "dist/ClaudeUsageBar-windows-$arch.zip"
Compress-Archive -Path (Join-Path $out 'ClaudeUsageBar.exe') -DestinationPath $zip -Force
Write-Host "Built $zip"

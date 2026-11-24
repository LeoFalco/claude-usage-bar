# Installs the latest Claude Usage Bar release for Windows and opens it.
# Usage: irm https://raw.githubusercontent.com/LeoFalco/claude-usage-bar/main/windows/install.ps1 | iex
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$url = "https://github.com/LeoFalco/claude-usage-bar/releases/latest/download/ClaudeUsageBar-windows-$arch.zip"
$dir = Join-Path $env:LOCALAPPDATA 'Programs\ClaudeUsageBar'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    Write-Host "Downloading $url"
    Invoke-WebRequest $url -OutFile (Join-Path $tmp 'app.zip') -UseBasicParsing
    Expand-Archive (Join-Path $tmp 'app.zip') $tmp -Force

    Get-Process ClaudeUsageBar -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    New-Item -ItemType Directory -Force $dir | Out-Null
    $exe = Join-Path $dir 'ClaudeUsageBar.exe'
    Copy-Item (Join-Path $tmp 'ClaudeUsageBar.exe') $exe -Force
    # Downloaded files carry the Mark of the Web; the app is not code-signed, so clear it to avoid SmartScreen.
    Unblock-File $exe

    Start-Process $exe
    Write-Host "Installed $exe. Look for the usage number in the system tray (it may be under the ^ overflow)."
} finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

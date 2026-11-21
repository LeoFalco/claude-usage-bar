#!/usr/bin/env bash
# Installs the latest Claude Usage Bar release into /Applications and opens it.
# Usage: curl -fsSL https://raw.githubusercontent.com/LeoFalco/claude-usage-bar/main/install.sh | bash
set -euo pipefail

APP=/Applications/ClaudeUsageBar.app
URL=https://github.com/LeoFalco/claude-usage-bar/releases/latest/download/ClaudeUsageBar.zip
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

echo "Downloading $URL"
curl -fsSL "$URL" -o "$TMP/app.zip"
ditto -x -k "$TMP/app.zip" "$TMP"

pkill -x ClaudeUsageBar 2>/dev/null || true
rm -rf "$APP"
mv "$TMP/ClaudeUsageBar.app" "$APP"
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true

open "$APP"
echo "Installed $APP. Look for ✳︎ in the menu bar."

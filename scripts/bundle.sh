#!/usr/bin/env bash
# Builds ClaudeUsageBar.app (and a zip of it) into dist/.
# Pass extra `swift build` flags, e.g. `scripts/bundle.sh --arch arm64 --arch x86_64` for a universal build.
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${VERSION:-0.0.0-dev}"
swift build -c release "$@"
BIN="$(swift build -c release "$@" --show-bin-path)/ClaudeUsageBar"

APP=dist/ClaudeUsageBar.app
rm -rf dist && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
cp "$BIN" "$APP/Contents/MacOS/ClaudeUsageBar"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleExecutable</key><string>ClaudeUsageBar</string>
  <key>CFBundleIdentifier</key><string>io.github.leofalco.claude-usage-bar</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundleName</key><string>Claude Usage Bar</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSUIElement</key><true/>
</dict></plist>
PLIST
# shortcut: ad-hoc signature only (users must clear quarantine); add Developer ID signing + notarization if this gets real users.
codesign --force --sign - "$APP"
(cd dist && ditto -c -k --keepParent ClaudeUsageBar.app ClaudeUsageBar.zip)
echo "Built $APP"

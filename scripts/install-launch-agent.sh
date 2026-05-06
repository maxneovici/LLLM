#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_BIN="$ROOT_DIR/artifacts/lllm-osx-arm64/LLLM"
PLIST="$HOME/Library/LaunchAgents/com.maxneovici.lllm.plist"

if [[ ! -x "$APP_BIN" ]]; then
  echo "Missing published binary: $APP_BIN"
  echo "Run scripts/publish-macos.sh first."
  exit 1
fi

mkdir -p "$HOME/Library/LaunchAgents" "$ROOT_DIR/App_Data/logs"

cat > "$PLIST" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>com.maxneovici.lllm</string>
  <key>ProgramArguments</key>
  <array>
    <string>$APP_BIN</string>
    <string>--urls</string>
    <string>http://127.0.0.1:5288</string>
  </array>
  <key>WorkingDirectory</key>
  <string>$ROOT_DIR</string>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <true/>
  <key>StandardOutPath</key>
  <string>$ROOT_DIR/App_Data/logs/launchd.out.log</string>
  <key>StandardErrorPath</key>
  <string>$ROOT_DIR/App_Data/logs/launchd.err.log</string>
  <key>EnvironmentVariables</key>
  <dict>
    <key>ASPNETCORE_ENVIRONMENT</key>
    <string>Production</string>
  </dict>
</dict>
</plist>
PLIST

launchctl unload "$PLIST" >/dev/null 2>&1 || true
launchctl load "$PLIST"

echo "Installed and loaded $PLIST"
echo "Open http://127.0.0.1:5288"

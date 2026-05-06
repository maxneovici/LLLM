#!/usr/bin/env bash
set -euo pipefail

PLIST="$HOME/Library/LaunchAgents/com.maxneovici.lllm.plist"

if [[ -f "$PLIST" ]]; then
  launchctl unload "$PLIST" >/dev/null 2>&1 || true
  rm "$PLIST"
  echo "Removed $PLIST"
else
  echo "LaunchAgent is not installed."
fi

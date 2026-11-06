#!/bin/bash
# Installs the on-demand runner dispatcher on a Mac (see dispatcher.py):
#   curl -fsSL https://raw.githubusercontent.com/yevhenii-sir/unity-ci/v1/runner/install.sh | bash
# Run it again to update the dispatcher; the config and the token are kept.
set -euo pipefail

REF="${UNITY_CI_REF:-v1}"
ROOT="$HOME/.unity-ci-runner"
AGENT="com.yevhenii.unity-ci-runner"
PLIST="$HOME/Library/LaunchAgents/$AGENT.plist"
LOGS="$HOME/Library/Logs/unity-ci-runner"

mkdir -p "$ROOT" "$LOGS" "$HOME/Library/LaunchAgents"
curl -fsSL "https://raw.githubusercontent.com/yevhenii-sir/unity-ci/$REF/runner/dispatcher.py" -o "$ROOT/dispatcher.py"

if [ ! -f "$ROOT/config.json" ]; then
  printf 'Fine-grained GitHub token (Administration: read/write, Actions: read, Contents: read): '
  read -rs token < /dev/tty
  echo
  ( umask 077
    python3 -c 'import json, sys; json.dump({"token": sys.argv[1], "repos": [], "max_runners": 2}, open(sys.argv[2], "w"), indent=2)' \
      "$token" "$ROOT/config.json" )
fi
chmod 600 "$ROOT/config.json"

python3 "$ROOT/dispatcher.py" --check
python3 "$ROOT/dispatcher.py" --setup

cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$AGENT</string>
  <key>ProgramArguments</key>
  <array>
    <string>/usr/bin/python3</string>
    <string>$ROOT/dispatcher.py</string>
  </array>
  <key>WorkingDirectory</key><string>$ROOT</string>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>ProcessType</key><string>Interactive</string>
  <key>StandardOutPath</key><string>$LOGS/dispatcher.log</string>
  <key>StandardErrorPath</key><string>$LOGS/dispatcher.log</string>
</dict>
</plist>
EOF

launchctl bootout "gui/$(id -u)/$AGENT" 2>/dev/null || true
launchctl bootstrap "gui/$(id -u)" "$PLIST"
echo "Dispatcher running; log: $LOGS/dispatcher.log"

#!/bin/zsh
# Deploy DriverWatch to a Windows host: copy the source, compile it there with
# the .NET Framework csc (C# 5, /target:winexe so there is no console window),
# register the logon task, and launch it.
#
#   ./Scripts/deploy.sh [ssh-host]        (default: bellatrix)
#
# Mirrors the Bezel agent deploy: needs passwordless SSH and a logged-in console
# session on the host. The running app holds DriverWatch.exe open, so it is
# stopped before the rebuild.
set -euo pipefail

HOST="${1:-bellatrix}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
CSC='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

echo "==> copying to $HOST"
ssh "$HOST" 'powershell -NoProfile -Command "New-Item -ItemType Directory -Force -Path $env:USERPROFILE\DriverWatch | Out-Null"'
scp -q "$HERE/DriverWatch.cs" "$HERE/Scripts/install.ps1" "$HERE/Scripts/compile.ps1" "$HOST:DriverWatch/"

echo "==> compiling on $HOST"
ssh "$HOST" 'powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME\DriverWatch\compile.ps1"'

echo "==> registering the logon task and launching"
ssh "$HOST" 'powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME\DriverWatch\install.ps1"'

echo
echo "Done. DriverWatch is in the tray on $HOST (no taskbar entry) and starts at logon."
echo "Its dashboard is on http://127.0.0.1:48620/ on that machine (open from the tray)."

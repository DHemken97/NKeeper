#!/bin/sh
set -e
Xvfb :1 -screen 0 1024x768x24 &
sleep 1
DISPLAY=:1 xterm &
exec x11vnc -display :1 -rfbport "${VNC_PORT:-5900}" -passwd "${VNC_PASSWORD:?}" -forever -shared -quiet

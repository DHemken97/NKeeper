#!/bin/sh
# Fake qm vncproxy: like the real one it needs root and a ticket. It greets with what it was asked
# for (VM, node and ticket), then echoes, standing in for the VM's display.
[ "$(id -u)" = 0 ] || { echo "please run as root" >&2; exit 1; }
[ "$1" = vncproxy ] || { echo "fake qm: unsupported call: $*" >&2; exit 1; }
[ -n "$LC_PVE_TICKET" ] || { echo "LC_PVE_TICKET not set, VNC proxy without password is forbidden" >&2; exit 2; }
printf 'vm %s on %s ticket %s\n' "$2" "${FAKE_PVE_NODE:-pve-test}" "$LC_PVE_TICKET"
exec cat

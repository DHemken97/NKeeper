#!/bin/sh
# Fake libvirt for NKeeper's tests: one running VM with SPICE on 127.0.0.1:5930, one shut off.
args="$*"
case "$args" in
  *"list --all --uuid"*) printf '11111111-1111-1111-1111-111111111111\n22222222-2222-2222-2222-222222222222\n' ;;
  *"domname 11111111-1111-1111-1111-111111111111"*) echo "web vm" ;;
  *"domname 22222222-2222-2222-2222-222222222222"*) echo "db-vm" ;;
  *"domstate 11111111-1111-1111-1111-111111111111"*) echo "running" ;;
  *"domstate 22222222-2222-2222-2222-222222222222"*) echo "shut off" ;;
  *"--include-password 11111111-1111-1111-1111-111111111111"*) echo "spice://127.0.0.1:5930/?password=vm-secret" ;;
  *"domdisplay"*"11111111-1111-1111-1111-111111111111"*) echo "spice://127.0.0.1:5930" ;;
  *"domdisplay"*) echo "error: Requested operation is not valid: domain is not running" >&2; exit 1 ;;
  *) echo "error: unsupported fake virsh call: $args" >&2; exit 1 ;;
esac

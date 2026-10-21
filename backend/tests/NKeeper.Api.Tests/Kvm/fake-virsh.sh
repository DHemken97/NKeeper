#!/bin/sh
# Fake libvirt for NKeeper's tests: a running VM with SPICE on 127.0.0.1:5930, one with VNC on the
# same port (display :30), and one shut off (which refuses power actions, as if not running).
args="$*"
# Power actions are logged (for the tests to check) and act on the running VM only.
case "$args" in
  *" start "*|*" shutdown "*|*" reboot "*|*" suspend "*|*" managedsave "*|*" resume "*|*" destroy "*|*" reset "*)
    echo "${args#-c qemu:///system }" >> /tmp/virsh-power.log
    case "$args" in
      *22222222-2222-2222-2222-222222222222*) echo "error: Requested operation is not valid: domain is not running" >&2; exit 1 ;;
    esac
    echo "Domain '${args##* }' done"; exit 0 ;;
esac
case "$args" in
  *"list --all --uuid"*) printf '11111111-1111-1111-1111-111111111111\n22222222-2222-2222-2222-222222222222\n33333333-3333-3333-3333-333333333333\n' ;;
  *"domname 11111111-1111-1111-1111-111111111111"*) echo "web vm" ;;
  *"domname 22222222-2222-2222-2222-222222222222"*) echo "db-vm" ;;
  *"domname 33333333-3333-3333-3333-333333333333"*) echo "vnc-vm" ;;
  *"domstate 11111111-1111-1111-1111-111111111111"*) echo "running" ;;
  *"domstate 22222222-2222-2222-2222-222222222222"*) echo "shut off" ;;
  *"domstate 33333333-3333-3333-3333-333333333333"*) echo "running" ;;
  *"--include-password 11111111-1111-1111-1111-111111111111"*) echo "spice://127.0.0.1:5930/?password=vm-secret" ;;
  *"domdisplay"*"11111111-1111-1111-1111-111111111111"*) echo "spice://127.0.0.1:5930" ;;
  *"--include-password 33333333-3333-3333-3333-333333333333"*) echo "vnc://:vnc-pw@127.0.0.1:30" ;;
  *"domdisplay"*"33333333-3333-3333-3333-333333333333"*) echo "vnc://127.0.0.1:30" ;;
  *"domdisplay"*) echo "error: Requested operation is not valid: domain is not running" >&2; exit 1 ;;
  *) echo "error: unsupported fake virsh call: $args" >&2; exit 1 ;;
esac

#!/bin/sh
# Fake drbdadm for NKeeper's tests: resources r0 and r1 are configured. Each one that is up has a
# file in /var/lib/fake-drbd holding this node's role; r0 starts up as Secondary, r1 down.
[ "$(id -u)" = 0 ] || { echo "drbdadm: must be root" >&2; exit 1; }
d=/var/lib/fake-drbd
[ "$1" = sh-resources ] && { echo "r0 r1"; exit 0; }
case "$2" in r0|r1) ;; *) echo "'$2' not defined in your config (for this host)." >&2; exit 10 ;; esac
case "$1" in
  up) [ -f $d/$2 ] && { echo "$2: Failure: (162) Invalid configuration request" >&2; exit 10; }; echo Secondary > $d/$2 ;;
  down) rm -f $d/$2 $d/$2.standalone ;;
  primary) [ -f $d/$2 ] || { echo "$2: No such resource" >&2; exit 10; }; echo Primary > $d/$2 ;;
  secondary) [ -f $d/$2 ] || { echo "$2: No such resource" >&2; exit 10; }; echo Secondary > $d/$2 ;;
  disconnect) touch $d/$2.standalone ;;
  connect) rm -f $d/$2.standalone ;;
  adjust) [ -f $d/$2 ] || echo Secondary > $d/$2 ;;
  *) echo "fake drbdadm: unsupported call: $*" >&2; exit 1 ;;
esac

#!/bin/sh
# Fake drbdadm for NKeeper's migration tests: resource r0 (/dev/drbd0); /var/lib/fake-drbd/r0
# holds this node's role while it is up. Its peer is always connected and in sync.
[ "$(id -u)" = 0 ] || { echo "drbdadm: must be root" >&2; exit 1; }
f=/var/lib/fake-drbd/$2
case "$1" in
  sh-resources) echo r0; exit 0 ;;
  net-options) f=/var/lib/fake-drbd/$3; echo "$2" >> /var/lib/fake-drbd/net-options; exit 0 ;;
esac
[ "$2" = r0 ] || { echo "'$2' not defined in your config (for this host)." >&2; exit 10; }
case "$1" in
  role) [ -f $f ] || { echo "r0: No such resource" >&2; exit 10; }; cat $f ;;
  sh-dev) echo /dev/drbd0 ;;
  dstate) echo UpToDate/UpToDate ;;
  cstate) echo Connected ;;
  up) echo Secondary > $f ;;
  down) rm -f $f ;;
  primary) [ -f $f ] || exit 10; echo Primary > $f ;;
  secondary) [ -f $f ] || exit 10; grep -q " /srv/vms " /var/lib/fake-mount/table && { echo "r0: State change failed: (-12) Device is held open by someone" >&2; exit 11; }; echo Secondary > $f ;;
  *) echo "fake drbdadm: unsupported call: $*" >&2; exit 1 ;;
esac

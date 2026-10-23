#!/bin/sh
# Fake drbdsetup status for NKeeper's migration tests (see fake-drbdadm).
[ "$(id -u)" = 0 ] || { echo "drbdsetup: must be root" >&2; exit 1; }
f=/var/lib/fake-drbd/r0
case "$*" in
  "status --json") [ -f $f ] || { echo '[]'; exit 0; }
    printf '[{"name":"r0","role":"%s","devices":[{"volume":0,"minor":0,"disk-state":"UpToDate"}],"connections":[{"name":"peer","connection-state":"Connected","peer-role":"Unknown","peer_devices":[{"volume":0,"replication-state":"Established","peer-disk-state":"UpToDate"}]}]}]\n' "$(cat $f)" ;;
  "status r0") [ -f $f ] || exit 10; echo "r0 role:$(cat $f)" ;;
  *) echo "fake drbdsetup: unsupported call: $*" >&2; exit 10 ;;
esac

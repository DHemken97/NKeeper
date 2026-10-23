#!/bin/sh
# Fake drbdsetup status --json for NKeeper's tests: the resources fake-drbdadm brought up, each
# with one volume and a peer, node2, that is resyncing from this node.
[ "$(id -u)" = 0 ] || { echo "drbdsetup: must be root" >&2; exit 1; }
[ "$*" = "status --json" ] || { echo "fake drbdsetup: unsupported call: $*" >&2; exit 1; }
d=/var/lib/fake-drbd
printf '['
sep=''
for r in r0 r1; do
  [ -f $d/$r ] || continue
  if [ -f $d/$r.standalone ]; then c='"connection-state":"StandAlone","peer-role":"Unknown","peer_devices":[{"volume":0,"replication-state":"Off","peer-disk-state":"DUnknown"}]'
  else c='"connection-state":"Connected","peer-role":"Secondary","peer_devices":[{"volume":0,"replication-state":"SyncSource","peer-disk-state":"Inconsistent","percent-in-sync":42.5}]'; fi
  printf '%s{"name":"%s","node-id":0,"role":"%s","suspended":false,"devices":[{"volume":0,"minor":%s,"disk-state":"UpToDate"}],"connections":[{"peer-node-id":1,"name":"node2",%s}]}' \
    "$sep" "$r" "$(cat $d/$r)" "$(echo $r | tr -d r)" "$c"
  sep=','
done
echo ']'

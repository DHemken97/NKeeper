#!/bin/sh
# Fake multipath for NKeeper's tests: mpatha over two paths (one failed), and a map without an
# alias. Flushing a map removes it; mpathb is "in use" and can't be flushed.
[ "$(id -u)" = 0 ] || { echo "multipath: need to be root" >&2; exit 1; }
d=/var/lib/fake-multipath
case "$*" in
  "-ll")
    [ -f $d/flushed-mpatha ] || cat <<'MAPS'
mpatha (36001405aaaaaaaaaaaaaaaaaaaaaaaaa) dm-0 LIO-ORG,disk01
size=10G features='0' hwhandler='1 alua' wp=rw
|-+- policy='service-time 0' prio=50 status=active
| `- 3:0:0:0 sdb 8:16 active ready running
`-+- policy='service-time 0' prio=0 status=enabled
  `- 4:0:0:0 sdc 8:32 failed faulty offline
MAPS
    cat <<'MAPS'
36001405bbbbbbbbbbbbbbbbbbbbbbbbb dm-1 LIO-ORG,disk02
size=2.0T features='1 queue_if_no_path' hwhandler='1 alua' wp=rw
`-+- policy='service-time 0' prio=50 status=active
  |- 5:0:0:1 sdd 8:48 active ready running
  `- 6:0:0:1 sde 8:64 active ready running
MAPS
    ;;
  "-r") : ;;
  "-f mpatha") touch $d/flushed-mpatha ;;
  "-f "*) echo "${2}: map in use" >&2; exit 1 ;;
  *) echo "fake multipath: unsupported call: $*" >&2; exit 1 ;;
esac

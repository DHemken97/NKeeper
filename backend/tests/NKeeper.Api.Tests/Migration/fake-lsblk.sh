#!/bin/sh
# Fake lsblk for NKeeper's migration tests: /dev/drbd0 on a local partition, and no iSCSI disks.
case "$*" in
  "-s -J -o NAME,KNAME,TYPE,TRAN /dev/drbd0") echo '{"blockdevices":[{"name":"drbd0","kname":"drbd0","type":"disk","tran":null,"children":[{"name":"sda3","kname":"sda3","type":"part","tran":null}]}]}' ;;
  "-dn -o KNAME,TRAN") echo "sda sata" ;;
  *) echo "fake lsblk: unsupported call: $*" >&2; exit 1 ;;
esac

#!/bin/sh
# Fake lsblk for NKeeper's migration tests: /dev/drbd0 and the root file system's
# /dev/sda2 on local partitions, and no iSCSI disks.
case "$*" in
  "-s -J -o NAME,KNAME,TYPE,TRAN /dev/drbd0") echo '{"blockdevices":[{"name":"drbd0","kname":"drbd0","type":"disk","tran":null,"children":[{"name":"sda3","kname":"sda3","type":"part","tran":null}]}]}' ;;
  "-s -J -o NAME,KNAME,TYPE,TRAN /dev/sda2") echo '{"blockdevices":[{"name":"sda2","kname":"sda2","type":"part","tran":null,"children":[{"name":"sda","kname":"sda","type":"disk","tran":"sata"}]}]}' ;;
  "-dn -o KNAME,TRAN") echo "sda sata" ;;
  *) echo "fake lsblk: unsupported call: $*" >&2; exit 1 ;;
esac

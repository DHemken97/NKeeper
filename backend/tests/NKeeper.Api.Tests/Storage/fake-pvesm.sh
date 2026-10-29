#!/bin/sh
# Fake pvesm for NKeeper's tests: adds LVM storage to /etc/pve/storage.cfg, changes a storage's
# nodes and removes storage, as the real one does (the cluster file system is just a folder here).
[ "$(id -u)" = 0 ] || { echo "pvesm: must be root" >&2; exit 1; }
cfg=/etc/pve/storage.cfg
case "$1 $2 $3 $4 $6 $8 ${10}" in
  "add lvm "*" --vgname --content --shared --nodes")
    grep -q "^[a-z]*: $3\$" $cfg && { echo "create storage failed: storage ID '$3' already defined" >&2; exit 255; }
    printf '\nlvm: %s\n\tvgname %s\n\tcontent %s\n\tnodes %s\n\tshared 1\n' "$3" "$5" "$7" "${11}" >> $cfg ;;
  "set "*" --nodes "*)
    grep -q "^[a-z]*: $2\$" $cfg || { echo "storage '$2' does not exist" >&2; exit 255; }
    awk -v id="$2" -v nodes="$4" '
      /^[a-z]+: / { if (cur && !done) print "\tnodes " nodes; cur = ($2 == id); done = 0 }
      cur && /^\tnodes / { print "\tnodes " nodes; done = 1; next }
      { print }
      END { if (cur && !done) print "\tnodes " nodes }' $cfg > $cfg.new && mv $cfg.new $cfg ;;
  "remove "*)
    grep -q "^[a-z]*: $2\$" $cfg || { echo "storage '$2' does not exist" >&2; exit 255; }
    awk -v id="$2" '/^[a-z]+: / { skip = ($2 == id) } !skip' $cfg > $cfg.new && mv $cfg.new $cfg ;;
  *) echo "fake pvesm: unsupported call: $*" >&2; exit 1 ;;
esac

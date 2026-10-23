#!/bin/sh
# Fake findmnt for NKeeper's migration tests (mounts are lines of fake-mount's table):
#   --target PATH   the mount holding the path, else the root file system on the local partition /dev/sda2;
#   -M MOUNTPOINT   the mounts exactly at the mount point, or nothing (exit 1).
# Prints JSON with -J, else the column -o names (SOURCE by default).
json=0 col=SOURCE exact=0
for a; do
    case "$prev" in -o) col=$a ;; esac
    case "$a" in -J) json=1 ;; -M) exact=1 ;; esac
    prev=$a; p=$a
done
t=/var/lib/fake-mount/table
if [ $exact = 1 ]; then
    line=$(awk -v p="$p" '$2 == p' $t | tail -n 1)
    [ -n "$line" ] || exit 1
else
    line=$(awk -v p="$p" 'index(p, $2 "/") == 1 || p == $2 { if (length($2) > n) { n = length($2); l = $0 } } END { print l }' $t)
    [ -n "$line" ] || line="/dev/sda2 / ext4 -"
fi
set -- $line
if [ $json = 1 ]; then printf '{"filesystems":[{"source":"%s","target":"%s","fstype":"%s","uuid":"%s"}]}\n' "$1" "$2" "$3" "$4"
elif [ "$col" = FSTYPE ]; then echo "$3"
else echo "$1"; fi

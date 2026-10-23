#!/bin/sh
# Fake umount for NKeeper's migration tests (see fake-mount): busy while a running VM's disk is on it.
[ "$(id -u)" = 0 ] || { echo "umount: only root can do that" >&2; exit 1; }
t=/var/lib/fake-mount/table; m=$1
line=$(awk -v m="$m" '$2 == m' $t); [ -n "$line" ] || { echo "umount: $m: not mounted." >&2; exit 32; }
for s in /var/lib/fake-virsh/*.state; do
  [ -f "$s" ] && [ "$(cat "$s")" = running ] && grep -q "<source file='$m/" "${s%.state}.xml" && { echo "umount: $m: target is busy." >&2; exit 32; }
done
dev=$(echo "$line" | cut -d' ' -f1); mkdir -p "/var/lib/fake-disk/${dev##*/}"; cp -a "$m/." "/var/lib/fake-disk/${dev##*/}/"; rm -rf "$m"/*
awk -v m="$m" '$2 != m' $t > $t.new; mv $t.new $t

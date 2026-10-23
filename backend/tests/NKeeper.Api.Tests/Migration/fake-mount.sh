#!/bin/sh
# Fake mount for NKeeper's migration tests: mounts are lines of /var/lib/fake-mount/table, and a
# device's files are kept in /var/lib/fake-disk/<device> while it isn't mounted. Like the kernel,
# a DRBD device can only be mounted while it is Primary; fail makes every mount fail.
[ "$(id -u)" = 0 ] || { echo "mount: only root can do that" >&2; exit 1; }
t=/var/lib/fake-mount/table
[ -f /var/lib/fake-mount/fail ] && { echo "mount: ${4:-$1}: wrong fs type, bad option, bad superblock." >&2; exit 32; }
[ "$1" = -t ] || { echo "mount: $1: can't find in /etc/fstab." >&2; exit 1; }
fs=$2; dev=$3; m=$4
case "$dev" in /dev/drbd*) grep -qx Primary /var/lib/fake-drbd/r0 2>/dev/null || { echo "mount: $m: $dev: Wrong medium type" >&2; exit 32; } ;; esac
mkdir -p "$m"; cp -a "/var/lib/fake-disk/${dev##*/}/." "$m/"; echo "$dev $m $fs 1111-2222" >> $t

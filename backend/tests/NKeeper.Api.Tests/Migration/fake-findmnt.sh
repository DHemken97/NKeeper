#!/bin/sh
# Fake findmnt --target for NKeeper's migration tests: the mount (see fake-mount) holding the path.
json=0; for a; do [ "$a" = -J ] && json=1; p=$a; done
line=$(awk -v p="$p" 'index(p, $2 "/") == 1 || p == $2 { if (length($2) > n) { n = length($2); l = $0 } } END { print l }' /var/lib/fake-mount/table)
[ -n "$line" ] || line="overlay / overlay -"
set -- $line
if [ $json = 1 ]; then printf '{"filesystems":[{"source":"%s","target":"%s","fstype":"%s","uuid":"%s"}]}\n' "$1" "$2" "$3" "$4"; else echo "$1"; fi

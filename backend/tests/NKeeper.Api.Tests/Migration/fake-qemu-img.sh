#!/bin/sh
# Fake qemu-img for NKeeper's migration tests: every image is a qcow2 of 1 GiB.
case "$1" in
  info) for a; do f=$a; done; [ -f "$f" ] || { echo "qemu-img: Could not open '$f'" >&2; exit 1; }
    printf '{"filename":"%s","format":"qcow2","virtual-size":1073741824,"actual-size":%s}\n' "$f" "$(stat -c %s "$f")" ;;
  create) for a; do case "$a" in -*|qcow2|raw) ;; *) [ -z "$f" ] && f=$a ;; esac; done; : > "$f" ;;
  *) echo "fake qemu-img: unsupported call: $*" >&2; exit 1 ;;
esac

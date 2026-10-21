#!/bin/sh
# Fake mountpoint -q for NKeeper's migration tests (see fake-mount).
for a; do m=$a; done
awk -v m="$m" '$2 == m { f = 1 } END { exit !f }' /var/lib/fake-mount/table

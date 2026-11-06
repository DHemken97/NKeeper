#!/bin/sh
# Fake multipathd for NKeeper's tests. Reconfiguring applies the configuration's aliases, like
# fake-multipath's reload. These files in its state folder change that: daemon-down (it isn't
# running), ignore-aliases (it doesn't apply them) and daemon-only (it answers first and applies
# them two seconds later, as the real daemon reconfigures after answering).
[ "$(id -u)" = 0 ] || { echo "multipathd: need to be root" >&2; exit 1; }
d=/var/lib/fake-multipath
[ "$*" = reconfigure ] || { echo "fake multipathd: unsupported call: $*" >&2; exit 1; }
if [ -f $d/daemon-down ]; then echo "can't connect to multipathd" >&2; exit 1; fi
[ -f $d/ignore-aliases ] && exit 0
if [ -f $d/daemon-only ]; then
  (sleep 2; multipath --fake-apply) < /dev/null > /dev/null 2>&1 &
else
  multipath --fake-apply
fi

#!/bin/sh
# Stands in for ausearch: records its arguments, and answers from /var/lib/fake-linux/audit.log, as
# the real one does: -a finds an event, "<no matches>" with exit 1 when there's nothing.
d=/var/lib/fake-linux
printf '%s\n' "$*" >> "$d/ausearch-args"
if [ "$(id -u)" != 0 ]; then echo "Error opening config file (Permission denied)" >&2; exit 1; fi
if [ -e "$d/no-audit-log" ]; then echo "Error opening /var/log/audit/audit.log (No such file or directory)" >&2; exit 1; fi
if [ "$1" = -a ]; then
  grep ":$2)" "$d/audit.log" && exit 0
  echo "<no matches>" >&2; exit 1
fi
if [ ! -s "$d/audit.log" ]; then echo "<no matches>" >&2; exit 1; fi
cat "$d/audit.log"

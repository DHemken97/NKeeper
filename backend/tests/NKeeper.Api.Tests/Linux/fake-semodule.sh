#!/bin/sh
# Stands in for semodule: -lfull, -i file.pp, -X priority -r name, keeping modules in a file as
# "priority name". Needs root like the real one.
d=/var/lib/fake-linux
if [ "$(id -u)" != 0 ]; then echo "libsemanage.semanage_create_store: Could not read from module store. (Permission denied)." >&2; exit 1; fi
priority=400
while [ $# -gt 0 ]; do
  case "$1" in
    -lfull) awk '{ printf "%s %-20s pp          \n", $1, $2 }' "$d/modules"; exit 0 ;;
    -X) priority=$2; shift 2 ;;
    -i)
      name=$(basename "$2" .pp)
      if [ -e "$d/fail-install" ]; then echo "libsepol: conflict in policy" >&2; exit 1; fi
      [ -e "$d/silent-install" ] && exit 0
      grep -v " $name\$" "$d/modules" > "$d/modules.new"; echo "$priority $name" >> "$d/modules.new"; mv "$d/modules.new" "$d/modules"
      exit 0 ;;
    -r)
      if ! grep -q "^$priority $2\$" "$d/modules"; then echo "libsemanage.semanage_direct_remove_key: Unable to remove module $2 at priority $priority. (No such file or directory)." >&2; exit 1; fi
      grep -v "^$priority $2\$" "$d/modules" > "$d/modules.new"; mv "$d/modules.new" "$d/modules"
      exit 0 ;;
    *) shift ;;
  esac
done

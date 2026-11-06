#!/bin/sh
# Fake multipath for NKeeper's tests: mpatha over two paths (one failed), and a map without an
# alias. Flushing a map removes it; mpathb is "in use" and can't be flushed. Like the real tool,
# a reload takes the maps' names from the configuration's aliases. These files in its state folder
# change that: ignore-aliases (nothing applies them), refuse-reload (it fails without applying them),
# reload-errors (it applies them but still fails) and daemon-only (only multipathd applies them).
[ "$(id -u)" = 0 ] || { echo "multipath: need to be root" >&2; exit 1; }
d=/var/lib/fake-multipath
aaaa=36001405aaaaaaaaaaaaaaaaaaaaaaaaa
bbbb=36001405bbbbbbbbbbbbbbbbbbbbbbbbb

# The alias in the WWID's "multipath { }" block, if there is one.
configured() {
  cat /etc/multipath.conf /etc/multipath/conf.d/*.conf 2>/dev/null | awk -v w="$1" '
    $1 == "multipath" { w0 = ""; a0 = "" }
    $1 == "wwid" { w0 = $2; gsub(/"/, "", w0) }
    $1 == "alias" { a0 = $2; gsub(/"/, "", a0) }
    $1 ~ /^}/ { if (w0 == w && a0 != "") found = a0; w0 = ""; a0 = "" }
    END { if (found != "") print found }'
}

apply_aliases() {
  for w in $aaaa $bbbb; do
    a=$(configured $w)
    if [ -n "$a" ]; then echo "$a" > "$d/alias-$w"; else rm -f "$d/alias-$w"; fi
  done
}

# The map's first line: its alias (from the last reload, or its default) with the WWID, or just the WWID.
header() {
  name=$3
  [ -f "$d/alias-$1" ] && name=$(cat "$d/alias-$1")
  if [ -n "$name" ]; then echo "$name ($1) $2"; else echo "$1 $2"; fi
}

case "$*" in
  "-ll")
    if [ ! -f $d/flushed-mpatha ]; then
      header $aaaa "dm-0 LIO-ORG,disk01" mpatha
      cat <<'MAPS'
size=10G features='0' hwhandler='1 alua' wp=rw
|-+- policy='service-time 0' prio=50 status=active
| `- 3:0:0:0 sdb 8:16 active ready running
`-+- policy='service-time 0' prio=0 status=enabled
  `- 4:0:0:0 sdc 8:32 failed faulty offline
MAPS
    fi
    header $bbbb "dm-1 LIO-ORG,disk02" ""
    cat <<'MAPS'
size=2.0T features='1 queue_if_no_path' hwhandler='1 alua' wp=rw
`-+- policy='service-time 0' prio=50 status=active
  |- 5:0:0:1 sdd 8:48 active ready running
  `- 6:0:0:1 sde 8:64 active ready running
MAPS
    ;;
  "-r")
    if [ -f $d/refuse-reload ]; then echo "reload: device or resource busy" >&2; exit 1; fi
    if [ -f $d/ignore-aliases ] || [ -f $d/daemon-only ]; then exit 0; fi
    apply_aliases
    if [ -f $d/reload-errors ]; then echo "another map: failed to reload" >&2; exit 1; fi
    ;;
  "--fake-apply") apply_aliases ;;
  "-f mpatha") touch $d/flushed-mpatha ;;
  "-f "*) echo "${2}: map in use" >&2; exit 1 ;;
  *) echo "fake multipath: unsupported call: $*" >&2; exit 1 ;;
esac

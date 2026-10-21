#!/bin/sh
# Fake libvirt for NKeeper's migration tests: each domain is <uuid>.xml and <uuid>.state in
# /var/lib/fake-virsh. Like libvirt, a domain only starts when its disk's file is there.
d=/var/lib/fake-virsh
[ "$1" = -c ] && shift 2
cmd=$1; shift
for a; do case "$a" in --*) ;; *) u=$a ;; esac; done
need() { [ -f "$d/$u.xml" ] || { echo "error: failed to get domain '$u'" >&2; exit 1; }; }
disk() { sed -n "s:.*<source file='\(.*\)'/>.*:\1:p" "$d/$u.xml"; }
case "$cmd" in
  list) for f in "$d"/*.xml; do [ -f "$f" ] && basename "$f" .xml; done; exit 0 ;;
  domname) need; sed -n 's:.*<name>\(.*\)</name>.*:\1:p' "$d/$u.xml" ;;
  domstate) need; cat "$d/$u.state" ;;
  domdisplay) need ;;
  dominfo) need; echo "State:          $(cat "$d/$u.state")"; echo "Managed save:   no" ;;
  domblklist) need; echo " Type   Device   Target   Source"; echo "------------------------------------"; echo " file   disk     vda      $(disk)" ;;
  shutdown) need; echo "shut off" > "$d/$u.state"; echo "Domain '$u' is being shutdown" ;;
  start) need; f=$(disk); [ -e "$f" ] || { echo "error: Cannot access storage file '$f': No such file or directory" >&2; exit 1; }
    echo running > "$d/$u.state"; echo "Domain '$u' started" ;;
  dumpxml) need; cat "$d/$u.xml" ;;
  define) n=$(sed -n 's:.*<uuid>\(.*\)</uuid>.*:\1:p' "$u"); cp "$u" "$d/$n.xml"; echo "shut off" > "$d/$n.state"; chmod 644 "$d/$n".*; echo "Domain defined from $u" ;;
  undefine) need; rm -f "$d/$u.xml" "$d/$u.state"; echo "Domain '$u' has been undefined" ;;
  *) echo "fake virsh: unsupported call: $cmd $*" >&2; exit 1 ;;
esac

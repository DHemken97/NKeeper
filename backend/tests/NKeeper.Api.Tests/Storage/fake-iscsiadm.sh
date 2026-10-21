#!/bin/sh
# Fake iscsiadm for NKeeper's tests. Known targets ("portal,group target") are kept in nodes and
# logged-in sessions ("portal target") in sessions. Discovery at 10.0.0.9 finds a third target;
# other portals don't answer. Like the real one it needs root. With a "silent" file, logins exit 0
# without logging in (as a misconfigured initiator can).
[ "$(id -u)" = 0 ] || { echo "iscsiadm: must be run as root" >&2; exit 1; }
d=/var/lib/fake-iscsi
args="$*"
case "$args" in
  "-m session -P 3")
    [ -s $d/sessions ] || { echo "iscsiadm: No active sessions." >&2; exit 21; }
    echo "iSCSI Transport Class version 2.0-870"
    echo "version 2.1.8"
    n=0
    while read -r portal target; do
      n=$((n + 1))
      printf 'Target: %s (non-flash)\n\tCurrent Portal: %s,1\n\tPersistent Portal: %s,1\n' "$target" "$portal" "$portal"
      printf '\t\t**********\n\t\tInterface:\n\t\t**********\n\t\tIface Name: default\n'
      printf '\t\tiSCSI Connection State: LOGGED IN\n\t\tiSCSI Session State: LOGGED_IN\n\t\tInternal iscsid Session State: NO CHANGE\n'
      printf '\t\t************************\n\t\tAttached SCSI devices:\n\t\t************************\n'
      printf '\t\tHost Number: %s\tState: running\n\t\tscsi%s Channel 00 Id 0 Lun: 0\n' "$n" "$n"
      printf '\t\t\tAttached scsi disk sd%s\t\tState: running\n' "$(echo "$n" | tr 123 bcd)"
    done < $d/sessions ;;
  "-m node")
    [ -s $d/nodes ] || { echo "iscsiadm: No records found" >&2; exit 21; }
    cat $d/nodes ;;
  "-m discovery -t sendtargets -p 10.0.0.9:3260" | "-m discovery -t sendtargets -p 10.0.0.9")
    grep -q disk3 $d/nodes || echo "10.0.0.9:3260,1 iqn.2003-01.org.example.san:disk3" >> $d/nodes
    echo "10.0.0.9:3260,1 iqn.2003-01.org.example.san:disk3" ;;
  "-m discovery -t sendtargets -p "*)
    echo "iscsiadm: cannot make connection to ${args##* }: Connection refused" >&2; exit 4 ;;
  "-m session --rescan")
    echo "Rescanning session [sid: 1, target: iqn.2003-01.org.example.san:disk1, portal: 10.0.0.5,3260]" ;;
  "-m node -T "*" -p "*" --login")
    [ -f $d/silent ] && exit 0
    set -- $args; t=$4; p=$6
    grep -q " $t\$" $d/nodes || { echo "iscsiadm: No records found" >&2; exit 21; }
    echo "$p $t" >> $d/sessions
    echo "Login to [iface: default, target: $t, portal: $p] successful." ;;
  "-m node -T "*" -p "*" --logout")
    set -- $args; t=$4; p=$6
    grep -q "^$p $t\$" $d/sessions || { echo "iscsiadm: No matching sessions found" >&2; exit 21; }
    grep -v "^$p $t\$" $d/sessions > $d/sessions.new; mv $d/sessions.new $d/sessions
    echo "Logout of [sid: 1, target: $t, portal: $p] successful." ;;
  *) echo "fake iscsiadm: unsupported call: $args" >&2; exit 1 ;;
esac

#!/bin/sh
# Fake LVM and block-device tools for NKeeper's tests, one script installed under each name (pvs,
# pvcreate, vgcreate, vgchange, lvs, blkid, lsblk). Physical volumes ("device|vg") are kept in pvs;
# a volume group with an open-<vg> file has an open volume. The multipath maps are the fake
# multipath's: mpatha is dm-0 and 36001405bbbbbbbbbbbbbbbbbbbbbbbbb dm-1.
[ "$(id -u)" = 0 ] || { echo "$(basename "$0"): need to be root" >&2; exit 1; }
d=/var/lib/fake-lvm
touch $d/pvs
case "$(basename "$0") $*" in
  "pvs --noheadings --separator | -o pv_name,vg_name")
    sed 's/^/  /' $d/pvs ;;
  "pvcreate "*)
    grep -q "^$1|" $d/pvs && { echo "Can't initialize physical volume \"$1\": it is already one" >&2; exit 5; }
    echo "$1|" >> $d/pvs
    echo "  Physical volume \"$1\" successfully created." ;;
  "vgcreate "*)
    grep -q "^$2|\$" $d/pvs || { echo "  Device $2 isn't an unused physical volume." >&2; exit 5; }
    sed -i "s#^$2|\$#$2|$1#" $d/pvs
    echo "  Volume group \"$1\" successfully created" ;;
  "vgchange -an "*)
    [ -f $d/open-$2 ] && { echo "  Logical volume $2/vm-100-disk-0 in use." >&2; exit 5; }
    echo "  0 logical volume(s) in volume group \"$2\" now active" ;;
  "lvs --noheadings -o lv_name,lv_attr "*)
    [ -f $d/open-$4 ] && echo "  vm-100-disk-0 -wi-ao-----"
    exit 0 ;;
  "blkid -p "*)
    grep -q "^$2|" $d/pvs || exit 2
    echo "$2: UUID=\"x\" VERSION=\"LVM2 001\" TYPE=\"LVM2_member\" USAGE=\"raid\"" ;;
  "lsblk -Pdno KNAME,FSTYPE,PTTYPE")
    for m in dm-0:mpatha dm-1:36001405bbbbbbbbbbbbbbbbbbbbbbbbb; do
      t=''; grep -q "^/dev/mapper/${m#*:}|" $d/pvs && t=LVM2_member
      echo "KNAME=\"${m%%:*}\" FSTYPE=\"$t\" PTTYPE=\"\""
    done ;;
  *) echo "fake lvm: unsupported call: $(basename "$0") $*" >&2; exit 1 ;;
esac

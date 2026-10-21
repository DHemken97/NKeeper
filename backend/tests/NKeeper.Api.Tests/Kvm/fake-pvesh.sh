#!/bin/sh
# Fake Proxmox VE for NKeeper's tests: a cluster of this node (pve-test) and pve2, with a running
# VM, a stopped one, one with a serial display, one on pve2, a container and a template.
[ "$(id -u)" = 0 ] || { echo "please run as root" >&2; exit 1; }
case "$*" in
  "get /cluster/resources --type vm --output-format json")
    echo '[{"id":"qemu/100","type":"qemu","vmid":100,"name":"web","node":"pve-test","status":"running","template":0},{"id":"qemu/101","type":"qemu","vmid":101,"name":"db","node":"pve-test","status":"stopped"},{"id":"qemu/102","type":"qemu","vmid":102,"name":"serial-vm","node":"pve-test","status":"running"},{"id":"qemu/200","type":"qemu","vmid":200,"name":"remote","node":"pve2","status":"running"},{"id":"lxc/300","type":"lxc","vmid":300,"name":"ct","node":"pve-test","status":"running"},{"id":"qemu/9000","type":"qemu","vmid":9000,"name":"golden","node":"pve-test","status":"stopped","template":1}]' ;;
  "get /cluster/status --output-format json")
    echo '[{"id":"cluster","type":"cluster","name":"lab"},{"id":"node/pve-test","type":"node","name":"pve-test","ip":"127.0.0.1","online":1,"local":1},{"id":"node/pve2","type":"node","name":"pve2","ip":"10.0.0.2","online":1}]' ;;
  *) echo "fake pvesh: unsupported call: $*" >&2; exit 1 ;;
esac

#!/bin/sh
# Fake SSH between cluster nodes: runs the remote command here, as the node at root@<address>,
# after checking it is asked for the way Proxmox does it.
for a; do last=$a; done
case "$*" in
  *"-e none -T -o BatchMode=yes -o HostKeyAlias=pve2 -o UserKnownHostsFile=/etc/pve/nodes/pve2/ssh_known_hosts"*) ;;
  *) echo "fake ssh: unexpected options: $*" >&2; exit 255 ;;
esac
for a; do case "$a" in root@*) node=${a#root@} ;; esac; done
FAKE_PVE_NODE=$node exec sh -c "$last"

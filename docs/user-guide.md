# User guide

- [The workspace](#the-workspace)
- [Hosts](#hosts)
- [Importing from mRemoteNG](#importing-from-mremoteng)
- [Scanning hosts](#scanning-hosts)
- [Connecting](#connecting)
- [SSH](#ssh)
- [Telnet](#telnet)
- [Files (SFTP)](#files-sftp)
- [Storage (iSCSI, multipath, DRBD)](#storage-iscsi-multipath-drbd)
- [Jump hosts](#jump-hosts)
- [RDP](#rdp)
- [VNC](#vnc)
- [Web interfaces (HTTP/HTTPS)](#web-interfaces-httphttps)
- [Windows RPC](#windows-rpc)
- [KVM and Proxmox virtual machines (SPICE and VNC consoles)](#kvm-and-proxmox-virtual-machines-spice-and-vnc-consoles)
- [Proxmox consoles (through the Proxmox API)](#proxmox-consoles-through-the-proxmox-api)
- [Dell iDRAC (virtual console, status and power, RACADM)](#dell-idrac-virtual-console-status-and-power-racadm)
- [Trusting servers](#trusting-servers)
- [Health and Wake-on-LAN](#health-and-wake-on-lan)
- [Reports](#reports)
- [Your account](#your-account)

## The workspace

After sign-in, NKeeper is a single page in the spirit of mRemoteNG:
- **On the left:** the host tree, with search, group-by and sort.
- **On the right:** the selected host's details (or its editor) and a tab for each open session.

On a phone, the two panes show one at a time.

- **Search** matches every word against names, addresses, MAC, description, tags, services and account names:
  - `env:prod` matches a tag by name and value;
  - `site:"Datacenter A"` quotes a value with spaces;
  - `env:` means "has an env tag".
  - Clicking a tag on a host filters by it.
- **Group by** up to three levels of tag names, the service type, or **Runs on** (the hypervisor a VM runs on), e.g. *site → role* or *Runs on → role*. A host appears under every value it has. Hosts without the tag collect in a "No site" group. Under **Runs on**, a hypervisor heads its own group, followed by its VMs, since it runs on its own hardware (a nested hypervisor also appears under the host it runs on). Other hosts that run on no known host collect in "Not on a known host". Your last grouping choice is saved to your account and used by default on your next visit; a grouping in the page URL takes precedence.
- **Collapse all / Expand all** (the arrows next to **Refresh**, when the tree is grouped) closes or opens every group at once. After collapsing all, opening a group shows its subgroups still closed.
- **Sort** by name, address or last update. Names sort naturally, so `srv2` comes before `srv10`. Your last choice is saved to your account and used by default on your next visit; a sort in the page URL takes precedence.
- **Select several hosts** to change them together:
  - **Ctrl+click** (**⌘+click** on a Mac) adds or removes a host, and **Ctrl+Space** does the same from the keyboard;
  - **Shift+click** selects every host between the last one you clicked and this one, as the tree shows them;
  - **right-click a group** (or press the Menu key on it) for **Select N hosts** (everything under it, collapsed or not) or **Add to selection**.

  With two or more selected, the right pane lists the changes you can make to all of them at once:
  - **Add** a tag;
  - **Set** a tag, replacing every value it has;
  - **Remove** a tag (one value, or every value when you leave the value empty);
  - set or clear the host they **run on**, which also marks them as virtual machines.

  Each change says how many hosts it affects. **Apply** saves them all in one go, and hosts you can't manage are left out. A plain click on a host goes back to a single host.

  **Internal scan…** in the same pane scans every selected host. It works like a single internal scan, a few hosts at a time:
  - **Log in with** each host's own saved SSH login (hosts without one are skipped), or one username and password for all of them, used once and not saved.
  - **Host keys:** hosts NKeeper hasn't connected to before are skipped by default, so you can check their keys one at a time. Tick **Trust new SSH host keys** to accept them on first connection instead. Changed keys are always refused.
  - **Review:** the findings are listed host by host, with the same defaults as a single scan. VM links and new VMs found on any scanned hypervisor are listed once each, even when several nodes of a cluster report them. Hosts that weren't scanned are listed with the reason. **Apply** saves what you ticked.
- The selection, search, grouping and sort are part of the address (e.g. `/?group=site&q=env:prod&host=<id>`), so you can bookmark or share a view. Open sessions are not part of it.

## Hosts

A host has:
- a **name**;
- a **hostname and/or IP address**. When both are set, NKeeper connects to the IP address, and shows and copies it;
- an optional **MAC address** and **description**;
- its **hardware**: CPU cores (logical processors), memory and, for physical servers, the **serial number** (a Dell service tag). For a virtual machine, the CPU cores and memory are what its hypervisor gives it (vCPUs and allocated memory). Scans fill these in; you can also type them, with memory in GiB;
- whether it's a **virtual machine**, and if so, which host it **runs on** (its KVM or Proxmox node). The VM's details link to that host, and the host lists the VMs running on it. Deleting the host keeps its VMs and clears their link;
- optionally, a **jump host** to **reach it through** (see [Jump hosts](#jump-hosts));
- its own **tags**, **services** and **credentials**:
  - **Tags** are `name: value` pairs (the value is optional), e.g. `site: Datacenter A`, `env: production`, `role: sql`. A host can have the same tag name twice with different values. Tags are what you group, search and grant access by; hosts don't live in folders.
  - **Services** are the ways to connect: SSH, Telnet, RDP, RPC, VNC, HTTP, HTTPS, KVM, Proxmox and iDRAC. Those are the only types you can add. A host may still have an SSMS, SFTP, FTP, WinRM, PowerShell or Other service from an older version; its details mark it *Not supported*, and editing the host says it will be removed when you save (cancel to keep it). Each has an optional port and label. The first one is the default. An **iDRAC** service also has the iDRAC's own **address**, since it isn't the server's.
  - **Credentials** have a username, domain, label, password, SSH private key and notes. Passwords and keys are **write-only**: once saved, NKeeper only shows that one is stored. Leave the field empty when editing to keep it, or choose to clear it.

Administrators add hosts with **New host**. Anyone with *Manage* access can **Edit** or **Delete** a host.

**Clone** (in the host's right-click menu, or the button in its details; administrators) opens the editor for a **new** host pre-filled from the original:
- it has the same addresses, description, tags, services and credentials, and is named "… (copy)";
- change what differs (usually the name and address) and choose **Create host**;
- **Cancel** creates nothing;
- cloned credentials keep their stored passwords and keys, copied on the server without ever reaching your browser;
- trusted host keys and certificates aren't copied, since the clone is a different machine.

## Importing from mRemoteNG

Administrators can bring in an mRemoteNG connection file with **Import** (next to **New host**): your `confCons.xml`, or an XML file from mRemoteNG's **File ▸ Export**.

- **Master password:** enter it only if you set one in mRemoteNG. Files without one open on their own.
- **Matching:** a connection matches the host with the same **name** (case doesn't matter).
- **What to do** (any combination):
  - **Add** creates hosts for connections that match no host.
  - **Update** applies the file's address, description, MAC address, service, login and folder to matching hosts. Services, credentials and tags the file doesn't mention are kept.
  - **Delete** removes hosts whose names aren't in the file. It's off by default.
- **Preview first:** **Preview** shows what would be added, updated, deleted, left unchanged or skipped, and why. Nothing is saved until you choose **Import**, and changing an option asks for a new preview.
- **How connections map:**
  - each connection becomes a host with one service (its protocol and port) and, when it has a username or password, a credential. A protocol NKeeper can't connect with (PowerShell, raw sockets and the like) is left out with a warning; the host and its login are still imported;
  - the folder path becomes a `folder` tag, e.g. `folder: Servers/SQL`, so you can group by it;
  - values a connection inherits from its folders are resolved;
  - connections with the same name and address merge into one host with several services;
  - protocols NKeeper doesn't have (e.g. ICA, RAW) are stored as **Other** with the protocol as the label.
- **Skipped:** connections without a valid hostname or IP address, a second connection with the same name but a different address, and names shared by several NKeeper hosts (it's unclear which one to update).
- **Supported files:** mRemoteNG's current format (2.6) with its default AES-GCM encryption. Older files, or other encryption settings, are explained when you try them: re-save them in a current mRemoteNG.

## Scanning hosts

**Tasks ▸ Scan** (in a host's right-click menu) gathers inventory details. You need *Manage* access to the host. A scan only reports what it found: you then tick what to add, and **Apply** saves it as a normal edit of the host. Values that fill an empty field start ticked. Values that would replace one start unticked.

- **Internal** (**Tasks ▸ Scan ▸ Internal ▸ credential**) logs in to the host over SSH and reads its own settings. It reads:
  - the hostname and operating system;
  - the MAC address of the network interface NKeeper reached;
  - the TCP ports it listens on, except those that only listen on localhost;
  - the local accounts that can log in (a real shell; root and regular users);
  - the CPU cores (`nproc`), installed memory (`lsmem`, else the kernel's total) and, on physical hardware, the serial number. Only root can read the serial number on most systems: scan as root, or as a user with passwordless sudo, to get it.

  **On the network, an internal scan is a single SSH login**, like an administrator's, so it suits places that don't allow scanning. The commands only read local state and contact no other machines. It needs Linux or another Unix with `sh`.

  **Windows hosts** are scanned over an **RPC** service instead (**Tasks ▸ Scan ▸ Internal**, *Over RPC (WMI)*), with a Windows login and its domain. It makes read-only WMI queries: the computer name, manufacturer and model, the Windows version, network adapters, listening TCP ports, enabled local accounts, logical processors, installed memory (the memory modules added up) and the BIOS serial number. WMI uses RPC on port 135 plus Windows' dynamic RPC ports, so the firewall must allow those from the NKeeper server, and the account must be allowed remote WMI (an administrator is). On Linux, NKeeper uses Impacket for this: the Docker image and the Linux installer include it.
- **External** (**Tasks ▸ Scan ▸ External**) is run by the NKeeper server against the host's saved address, after you confirm. It reads:
  - which common ports are open (SSH, RDP, VNC, web, file sharing, mail, directory and database ports);
  - the SSH banner, which often names the operating system;
  - the reverse-DNS name;
  - the MAC address, but only when the host is on the same network segment as the NKeeper server (not through a router, VPN or container network).

  **Network monitoring will see it as a port scan.** It only ever scans that one host.
- **What can be added:**
  - the hostname, IP address and MAC address;
  - the operating system, as an `os` tag that you can group by;
  - a service for each open port NKeeper has a service for. Other open ports are listed for information;
  - a **KVM** service when libvirt is installed, or the host is a Proxmox VE node (internal scans);
  - each local account as a **credential without a password**. Using it asks for the password; set the password in the host's editor to save it;
  - **Virtual machine**, when the scan finds the host runs under a hypervisor (it asks `systemd-detect-virt`);
  - the **CPU cores**, **memory** and **serial number** (internal scans). A VM's serial number is invented by its hypervisor, so it's never offered.
- **Which VMs run where** (internal scans of hypervisors): scanning a node lists its VMs and containers and matches each to an NKeeper host. A match uses the VM identity recorded by an earlier scan, else a MAC address, else the name (or the hostname's first part). Each match is offered as a link: *app01 runs on node1*.
  - **KVM / libvirt:** the node's VMs, read with `virsh` (read-only if possible). VMs often move between nodes. A VM moves to the scanned node only if it's **running** there; one that is merely defined there (shared storage) stays linked where it runs. Scan each node to keep the links current.
  - **Proxmox:** the VM and container configurations are shared by the whole cluster, so scanning **any one node** links every VM in the cluster to the node it's on. Other nodes must be NKeeper hosts named like the Proxmox node (e.g. `pve2`, or `pve2.example.com`). Reading the configurations needs **root**; as another user the scan says which it couldn't read.
  - A link records when a scan saw the VM there (shown on the VM). A link you set by hand in the editor has no such time.
  - **What each VM is given** is recorded with its link: its vCPUs and memory, from libvirt (`virsh dominfo`) or the Proxmox configuration (cores × sockets, and memory). A linked VM whose allocation changed is offered again, as *Updates its resources to …*. A Proxmox container without a core limit has no vCPU count.
  - **VMs that aren't in NKeeper yet** can be added from the same scan (administrators). Tick **Add …** for each one you want; they start unticked, and **Select all** ticks them all. Each new host gets:
    - the VM's name;
    - an IP address the scan found, or the VM's name as its hostname when that's a valid DNS name;
    - its first MAC address;
    - a link to the node it runs on, with its VM identity.

    Add services and logins afterwards. Addresses come from the node itself: libvirt's DHCP leases and the node's ARP table (running VMs), and Proxmox containers' and cloud-init VMs' static addresses. A VM with no address and a name that isn't a hostname is listed but can't be added from the scan.

Changing a host's hostname or IP address forgets its trusted keys too, for the same reason.

## Connecting

Right-click a host in the tree, or press the Menu key or Shift+F10 on it, then choose **Connect ▸ service ▸ credential**. Each session opens as a tab next to the host details and keeps running while you switch tabs.

- **Double-click** a host to connect with its first connectable service and first suitable credential. If none fits, you're asked for a login.
- The **Connect** button in the host details opens the same menu, which is the way to connect on touch screens.
- **Enter credentials…** connects with a one-off login that is used once and never saved. Such sessions can't reconnect without typing it again.
- When a session ends normally (e.g. `exit`, or signing out of Windows), its tab closes. A session that fails stays open to show why, with **Try again** or **Reconnect**.
- Close a tab with its **×**, or middle-click it.

A credential that can't work for the service is shown greyed out with the reason, for example "No username". A credential without a stored password (or SSH key) shows **Asks for password**: choosing it asks for the password, with its username filled in, and uses it once. To stop being asked, edit the host and set the password.

**The NKeeper server makes every connection**, not your PC. Hosts must be reachable and resolvable from the server. Your passwords and keys are decrypted only there, never sent to your browser.

## SSH

A full terminal (xterm.js) that resizes with the tab.
- **Authentication:** a credential can use a password, a private key, or both. The password doubles as the key's passphrase. Keyboard-interactive prompts are answered with the password.
- **Domain accounts:** a credential's domain is sent as `DOMAIN\user`, which is what Windows OpenSSH expects.
- **Idle sessions** stay open.

## Telnet

For switches, routers, serial-console servers and other devices that only offer Telnet (port 23 by default). It's the same terminal as SSH:
- **Terminal:** NKeeper tells the device it's an `xterm-256color` terminal and keeps it informed of the window size, so full-screen menus fit the tab.
- **Logging in:**
  - Telnet has no login of its own: the device asks in the terminal.
  - With a saved credential, NKeeper answers the first `login:` (or `Username:`) and `Password:` prompts for you, once, just after connecting. A password-only credential answers just the password prompt.
  - **Without a login** (in the service's menu, and what double-click does when the host has no credential) connects and leaves the typing to you.
- **Not encrypted:** Telnet sends everything, passwords included, in the clear between NKeeper and the device. The status bar says **Unencrypted to host**. Your browser's connection to NKeeper is still protected. Prefer SSH wherever the device offers it.

## Files (SFTP)

Any host with an SSH service has **Connect ▸ Files ▸ credential** (or **Files ▸ SSH service ▸ credential** when it has several). The files open in a tab:
- **Browsing:** the tab starts in the login's home folder. Open a folder by clicking it, go back with **Up** or the path above the list, and **Refresh** to see changes made elsewhere.
- **Upload:** choose one or more files, up to 2 GB each. They go into the folder shown. If a file with the same name exists, NKeeper asks before replacing it.
- **Download:** files stream from the host through the NKeeper server to your browser.
- **New folder**, **Rename** and **Delete**. Delete asks first, and only deletes empty folders.
- **Logins and trust** are the same as SSH: the same credentials, host key check and trusted keys. The SSH server must have its SFTP subsystem enabled (OpenSSH does by default).
- **Audit:** every upload, download, rename, delete and new folder is in the audit log, with its path and size.

## Storage (iSCSI, multipath, DRBD)

Any Linux host with an SSH service has **Connect ▸ Storage ▸ credential** (or **Storage ▸ SSH service ▸ credential** when it has several). NKeeper runs the host's own tools over SSH and shows what it finds in a tab, one section for each tool that's installed:
- **iSCSI** (`iscsiadm`, from open-iscsi or iscsi-initiator-utils): the initiator's name, and every target it knows with its portal, session state and attached disks. **Discover** asks a portal (e.g. `10.0.0.5` or `10.0.0.5:3260`) for its targets (SendTargets). **Log in** and **Log out** each target, and **Rescan sessions** to see new LUNs.
- **Multipath** (`multipath`, from multipath-tools or device-mapper-multipath): each map with its WWID, device, vendor and size, and every path's device-mapper, checker and kernel state. A map is green when all its paths are ready. **Reload maps** after changing the configuration, and **Flush** a map that's no longer used.
- **DRBD** (`drbdadm` and `drbdsetup`): every configured resource, including those that are down, with this node's role, its disks' states and each peer's connection, role, replication and sync progress. Bring a resource **Up** or **Down**, make it **Primary** or **Secondary**, **Connect** or **Disconnect** its peers, or **Adjust** it to its configuration.
- **Asks first:** logging out, flushing, taking a resource down, changing its role and disconnecting it say what will happen and wait for you to confirm. The tools still refuse what would break something in use (a mounted map or a primary in use), and the tab shows their answer.
- **Root:** the tools need root. Sign in as root, or as a user who may use `sudo` without a password.
- **Logins and trust** are the same as SSH. Each change is recorded in the audit log (category *Storage*), with what it acted on.

## Jump hosts

A **jump host** (or bastion) is an SSH server that NKeeper signs in to first, then tunnels a host's connections through. Use it for sites, VLANs and customer networks that the NKeeper server can't reach directly.
- **Setting one:** edit the host and choose a host under **Reach through**, then which of its credentials to **sign in as** (by default, its first credential with a username and a password or key).
- **Which hosts can be jump hosts:** hosts with an SSH service that NKeeper reaches directly. There's one hop: a jump host can't have a jump host of its own, and a host that others go through can't get one.
- **What goes through it:** SSH, Telnet, files, web sessions, iDRAC (console, status and power, and RACADM), KVM consoles and internal SSH scans. The host details show **Reached through**, with a link to the jump host.
- **What doesn't yet:** RDP, VNC and RPC are greyed out in the Connect menu, since the RDP/VNC gateway dials hosts itself. External scans and health checks probe from the NKeeper server, so they skip these hosts.
- **Trust:** connect to the jump host with SSH once and trust its host key first; the tunnel can't stop to ask. The target's own host key or certificate is still checked against the target itself, so the jump host can't pretend to be it.
- **Access:** you need access to the jump host as well as the host. Without it, connecting says so.
- **Port forwarding:** the jump host's SSH server must allow TCP forwarding (`AllowTcpForwarding yes`, the OpenSSH default).

## RDP

Windows desktops through Apache Guacamole's guacd.
- **Resolution:** the remote desktop is sized to the tab, at your screen's pixel density.
- **Keyboard:** typing goes to the remote computer while the desktop has focus. **Ctrl+Alt+Del** and **Fullscreen** are in the status bar. In Chromium, full screen also captures Win and Alt+Tab.
- **Clipboard:** the text clipboard syncs both ways. Reading your clipboard needs browser permission and HTTPS (or `localhost`).
- **Sound** plays.
- **Off:** file transfer, drives, printing, microphone and recording.
- **Requirements:** RDP needs a credential with a username and password; the credential's domain is used as the Windows domain.
- **My sign-in:** if you signed in to NKeeper with your Active Directory account, RDP services also offer **My sign-in** (`CORP\you`), which logs in to the desktop as you. Your password stays on the NKeeper server. Double-clicking a host with an RDP service and no saved credential uses it too. If you changed your domain password since signing in, sign out of NKeeper and back in.
- **Security setting** (per RDP service): *Automatic* uses NLA when offered, otherwise TLS. You can also choose *NLA only* or *TLS only*. Insecure legacy RDP security is never used.

## VNC

Like RDP, through guacd. Most VNC servers ask only for a password, so a VNC credential needs a password and the username is optional. The desktop scales to the tab, the text clipboard syncs, and Ctrl+Alt+Del and Fullscreen are in the status bar.

Plain VNC doesn't encrypt. Your browser's connection to NKeeper is protected, but the hop from NKeeper to the host is not. The status bar says **Unencrypted to host**. Use VNC on trusted networks, or tunnel it.

## Web interfaces (HTTP/HTTPS)

For routers, iDRAC/iLO, NAS boxes, Proxmox and the like, **Connect ▸ HTTP/HTTPS** offers two ways:

- **Through NKeeper** (also what double-click does):
  - **Who connects:** the NKeeper server makes every request and relays the answers, so it works even when your PC can't reach the host.
  - **Where it shows:** the site appears inside its tab, in an isolated frame on its own address (`nkw-<id>.web.<server>`). It can't read your NKeeper session or navigate NKeeper away.
  - **The host's view:** it sees normal requests, with its own `Host`, `Origin` and `Referer`.
  - **Certificates:** a self-signed certificate is shown for you to trust once.
- **Directly in a new tab**: your browser opens the host's own address; NKeeper isn't involved.

A web session belongs to you and ends:
- when you close its tab or choose **End session**;
- after 30 minutes without activity, or 12 hours in total;
- immediately if your account is disabled or you lose access to the host.

**Reload** reloads the page inside the tab.

If web sessions don't load from other computers, the server needs the wildcard DNS record and certificate described in [Installation](installation.md#dns-for-web-sessions). Links in a page that hard-code the host's absolute address (e.g. `https://10.0.0.5/…`) leave the session and connect directly; most web interfaces use relative links.

## Windows RPC

An **RPC** service (port 135, the endpoint mapper) is mainly for [scanning Windows hosts](#scanning-hosts). **Connect ▸ RPC** also opens a low-level tool for administrators who know DCE/RPC. NKeeper binds the interface and signs in with NTLM using the credential and its domain. You then send calls as an operation number and a request stub in hex, and see the response stub or the fault. Calls are authenticated but not signed or encrypted.

## KVM and Proxmox virtual machines (SPICE and VNC consoles)

Add a **KVM** service (port 22 by default) to a hypervisor host, with an SSH credential. The same service works for libvirt hosts and Proxmox VE nodes: NKeeper finds out which one it is. Then **Connect ▸ KVM ▸ credential**:

- **The VM list:** like virt-manager, NKeeper connects over SSH, asks libvirt (`qemu:///system`) for the virtual machines, and lists each one's name, state and console type. **Refresh** updates the list.
- **The console:** **Open console** on a running VM with a SPICE or VNC display opens it in its own tab, with **Ctrl+Alt+Del** and **Fullscreen**.
- **Network path:** the console is tunnelled through NKeeper's SSH connection to the hypervisor, so the VM's console port can stay on the hypervisor's loopback.
- **Passwords:** the VM's console password, if set, is read from libvirt and used automatically. libvirt reveals it only to accounts with full (read-write) access to `qemu:///system`.
- **Resolution:** a SPICE display is shown at the VM's resolution, and scrolls if it's bigger than the tab. With the SPICE guest agent (`spice-vdagent`) installed in the VM, the resolution follows the tab's size. A VNC display is scaled to fit the tab.
- **One SPICE console at a time:** opening another SPICE console replaces the first. VNC consoles have no such limit.
- **On the hypervisor:** the SSH user must be able to use `qemu:///system` (usually by being in the `libvirt` group), and sshd must allow TCP forwarding (the OpenSSH default).
- **Not supported:** SPICE configured for TLS only, and displays that listen on a Unix socket instead of a port. When a VM has both a SPICE and a VNC display, NKeeper opens the one libvirt lists first.

**Proxmox VE nodes** work the same way, with these differences:

- **The VM list** is the whole cluster's, read with `pvesh`, with a **Node** column. Containers and templates aren't listed.
- **The console** is VNC, opened the way Proxmox's own console opens it: `qm vncproxy` relays the VM's display, with a one-time VNC password that NKeeper sets and uses for you. VMs on other nodes of the cluster are reached through the node you connected to, over the cluster's own SSH between nodes.
- **No console:** a VM whose display is a serial terminal (`vga: serial0`) or `none` has no console button.
- **On the node:** `pvesh` and `qm` need **root**. Sign in as root, or as a user who may use `sudo` without a password. TCP forwarding isn't needed.
- **Idle consoles:** Proxmox ends a console after 15 minutes with nothing happening on it. **Reconnect** opens it again.

## Proxmox consoles (through the Proxmox API)

Add a **Proxmox** service (HTTPS, port 8006 by default) to a Proxmox VE node, with a Proxmox login. Scans add one for an open port 8006.

- **The node itself:** **Connect ▸ Proxmox** opens the node's web interface, through NKeeper or directly in a new browser tab, like any HTTPS service.
- **A VM or container:** a host that a scan linked to its Proxmox node (see *Which VMs run where*) has **Connect ▸ Proxmox console ▸ login**. The logins are the node's, for its Proxmox service. The console opens in the VM's own tab, with **Ctrl+Alt+Del** and **Fullscreen**.
- **How it connects:** the way Proxmox's own console does. NKeeper's server signs in to the node's API, asks it for a VNC proxy (`vncproxy`), and relays the proxy's WebSocket to the tab. Any node of the cluster serves every VM, so it doesn't matter which node a VM moved to. Containers get their terminal console the same way.
- **Logins:** a Proxmox user, e.g. `root`, `root@pam` or `admin@pve`. Without a realm, the credential's **domain** is used as the realm, else `pam`. An **API token** works too: `user@realm!tokenid` as the username and the token's secret as the password. Accounts with two-factor sign-in can't be used; give NKeeper an API token instead.
- **Tasks ▸ Proxmox ▸ action ▸ login** runs a power action or migration on the VM or container:
  - **Migrate…** moves it to another node you choose. A running VM can move live; a running container is restarted on the new node. Disks on the node's local storage can be copied along.
  - **Shut down** and **Reboot** ask its operating system (a VM needs ACPI or the guest agent). **Pause** freezes it in memory. **Hibernate** saves its memory to disk and stops it. **Stop** and **Reset** act at once, like the power and reset buttons, and lose anything unsaved.
  - **Resume** continues a paused one; **Start** starts a stopped or hibernated one.
  - The dialog shows its current state and asks first, then follows Proxmox's task until it ends. Closing the dialog doesn't stop the task. Containers can't hibernate or reset.
  - Each action is recorded in the audit log. Users with *Use* access to the VM get these too: what the Proxmox login may do is up to Proxmox.
- **Permissions:** the account needs **VM.Console** on the VM to open its console, **VM.PowerMgmt** for power actions, and **VM.Migrate** to migrate (and to see it). It doesn't need root, SSH or sudo.
- **Certificate:** the node's certificate is trusted on first use, like any HTTPS service (it's shared with the node's web sessions).
- **Reconnecting:** each console is good for one connection. **Reconnect** opens a new one.
- **The node** must be an NKeeper host you can see, with a Proxmox service. Otherwise there's no Proxmox console or Proxmox task entry.

## Dell iDRAC (virtual console, status and power, RACADM)

Add an **iDRAC** service to the server's host, with the **iDRAC's IP address** (it has its own, separate from the server's). The port is the iDRAC's HTTPS port, 443 by default. Add the iDRAC login (e.g. `root`) as a credential with a password. Then **Connect ▸ iDRAC** offers:

- **Virtual console:** the iDRAC's HTML5 console in a tab, like an RDP or VNC session. NKeeper asks the iDRAC over Redfish for one-time console credentials and relays the console page, so your browser never contacts the iDRAC and the iDRAC password stays on the server. **Reload** opens a fresh console. Double-clicking a host whose first service is an iDRAC opens its console.
- **Status & power:** the server as Redfish reports it: model, service tag, power state, health, CPUs, memory, BIOS and iDRAC firmware. **Save to host** (with *Manage* access) records the service tag as the serial number, along with the CPU threads and memory, on the host. The **Power** buttons are the actions the server accepts: *Power on*, *Shut down* and *Restart* (through the operating system), *Reset* and *Power off* (immediate), *Power cycle*. Everything but *Power on* asks for confirmation. The tab also has buttons for the console, RACADM and the web interface.
- **RACADM:** a terminal on the iDRAC's SSH command line (port 22 of the iDRAC's address), where `racadm` commands run.
- **Web interface:** the iDRAC's own web pages, through NKeeper or directly, as for [web interfaces](#web-interfaces-httphttps).

Requirements and limits:
- **The console** needs iDRAC9 with firmware 4.x or later, a license that includes the virtual console (Enterprise or Datacenter), the virtual console enabled, and its **plug-in type set to HTML5 or eHTML5** (iDRAC Settings ▸ Services ▸ Virtual Console). When one is missing, *Status & power* says which, and the console explains why it can't open. For older iDRACs, open the web interface and launch the console from there.
- **Web sessions** must work on your server (see [Installation](installation.md#dns-for-web-sessions)), since the console is one.
- **Status and power** use standard Redfish, so they also work with other Redfish BMCs (e.g. HPE iLO 5 or later). The virtual console and RACADM are Dell-specific.
- **The iDRAC account** decides what's allowed: power control needs an account with that privilege on the iDRAC.
- **Certificates:** the iDRAC's self-signed certificate is shown for you to trust once, and covers Redfish, the console and the web interface. Its SSH host key (for RACADM) is trusted separately. Both are kept apart from the server's own keys, and are forgotten when the iDRAC's address changes.

## Trusting servers

NKeeper verifies whom it connects to, using **trust on first use**:
- **What you're asked:** the first connection to a server shows its identity. That's an SSH host key fingerprint (`SHA256:…`, as `ssh-keygen -lf` prints it), or an RDP or HTTPS certificate fingerprint. Compare it with the server's real one, then choose **Trust and connect**.
- **Where it's kept:** trusted keys are stored per host and listed in its details. SSH sessions and KVM share SSH keys.
- **If it changes:** NKeeper **refuses to connect** and says so, because a reinstalled server looks exactly like an attacker in the middle. When the change is expected (a rebuild, or a renewed certificate), an administrator or anyone with *Manage* access uses **Forget trusted keys…** in the host details, then connects again.

## Health and Wake-on-LAN

The NKeeper server checks every host's services every 5 minutes, the way a session would reach them:
- **How:** a TCP connection to each service's port. For HTTPS, iDRAC and RDP services, it also reads the TLS certificate, to track when it expires. It never signs in, and uses no credentials.
- **In the tree:** a dot before each host's name: green when every service answers, amber when some don't, red when none do. Hosts not checked yet have no dot.
- **In the details:** each service shows *Up · 12 ms*, or *Down since …* with the reason, and its certificate's expiry. Expiries within 30 days are in red. **Check now** checks the host again at once.
- **Reports ▸ Health:** what's down, longest first, and every certificate, soonest to expire first.

The checks come from the server, so a host can be up for you but down for NKeeper (or the other way round) when firewalls differ. Change the interval, or turn checks off, with `Health__*` in [Configuration](configuration.md#api-settings).

**Wake-on-LAN:** for a host with a MAC address, **Wake** (in its details or its menu) sends a magic packet from the NKeeper server, to UDP port 9. It goes to the local broadcast address, to the host's /24 broadcast address and to the host's own address. It's recorded in the audit log.
- **Same network:** it works when the server is on the host's network.
- **Routed networks:** the router must forward directed broadcasts.
- **Docker:** a container's default network doesn't carry broadcasts onto your LAN. Run the backend with host networking, or wake hosts across a router that forwards directed broadcasts.

## Reports

**Reports** in the top bar lists NKeeper's reports. Each one covers only the hosts you can see.

**VM hosts & resources** shows how your hypervisors are used:
- **Each VM host** has a row with its serial number, CPU cores and memory. It also shows what its VMs are given in total, as a share of the host: e.g. *24 (75%)* vCPUs and *96 GiB (75%)* memory.
- **Its VMs** are listed beneath it, each with its vCPUs and memory and its share of the host.
- **Overcommitted** values are shown in red: VMs given more than the host has (common for vCPUs, risky for memory).
- **Unknown values:** a VM whose vCPUs or memory aren't known is counted as *+1 unknown* rather than guessed. Scan the hypervisor to record them.
- **Other rows:** physical servers that run no VMs are listed after the VM hosts with their serial numbers, then VMs whose host isn't known.
- **Download CSV** saves one row per host.

The figures come from the hosts' hardware fields: scan the hypervisors (internal scan) to fill in each VM's allocation, and scan the hosts themselves, or use the iDRAC's **Save to host**, for their cores, memory and serial number.

**IP address map** is a spreadsheet of your address space:
- **Columns** are /24 subnets, one for each subnet that has at least one host (e.g. `10.0.1.x`, `192.168.1.x`, `192.168.42.x`), in numeric order. Each header shows how many of the subnet's addresses are used.
- **Rows** are the last octet, `.0` to `.255`.
- **Cells** show the name of the host at that address. Hover for its hostname, and click to open it in the workspace. An address used by two or more hosts is highlighted as a conflict.
- **Only used addresses** hides rows that are empty in every subnet. **Download CSV** saves the table, as shown, for Excel or another spreadsheet.
- Hosts with only a hostname or an IPv6 address aren't in the map. The summary line counts them.

## Your account

**Signing in.** Use your NKeeper username or email and password, or your domain account if Active Directory sign-in is set up. If the sign-in page shows **Sign in with …** (for example *Sign in with Microsoft*), you can use your organization's account instead: NKeeper sends you to its sign-in page and back. **Remember me** applies to both.

**Sign-in codes.** If an administrator turned on **Email 2FA challenge code** for your account, NKeeper emails you a 9-digit code after your password (or single sign-on). Enter it on the **Check your email** page. Spaces or dashes are fine. The code works once, for 10 minutes. **Send a new code** replaces it if the email doesn't arrive. After five wrong codes, sign in again. If no code arrives at all, ask an administrator to check the email settings.

The account menu (top right) has **Change password** and **Sign out**. Changing your password signs out your other sessions. If you sign in with your Active Directory account, change your password in Windows instead. Accounts that use single sign-on have no NKeeper password: their password is managed by the identity provider.

If an administrator set a temporary password, NKeeper asks you to choose your own at your first sign-in. There's no self-service password reset; ask an administrator.

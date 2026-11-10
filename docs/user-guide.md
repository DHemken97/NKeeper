# User guide

- [The workspace](#the-workspace)
- [Keyboard shortcuts](#keyboard-shortcuts)
- [Hosts](#hosts)
- [Importing from mRemoteNG](#importing-from-mremoteng)
- [Scanning hosts](#scanning-hosts)
- [Connecting](#connecting)
- [SSH](#ssh)
- [Telnet](#telnet)
- [Files (SFTP)](#files-sftp)
- [Storage (iSCSI, multipath, DRBD)](#storage-iscsi-multipath-drbd)
- [Event Log Viewer (Windows)](#event-log-viewer-windows)
- [Journal (journalctl)](#journal-journalctl)
- [SELinux](#selinux)
- [Microsoft SQL Server](#microsoft-sql-server)
- [Oracle](#oracle)
- [SAP ASE and Replication Server](#sap-ase-and-replication-server)
- [Jump hosts](#jump-hosts)
- [RDP](#rdp)
- [VNC](#vnc)
- [Web interfaces (HTTP/HTTPS)](#web-interfaces-httphttps)
- [Windows RPC](#windows-rpc)
- [KVM and Proxmox virtual machines (SPICE and VNC consoles)](#kvm-and-proxmox-virtual-machines-spice-and-vnc-consoles)
- [Proxmox consoles (through the Proxmox API)](#proxmox-consoles-through-the-proxmox-api)
- [Migrating VMs with their storage](#migrating-vms-with-their-storage)
- [Dell iDRAC (virtual console, status and power, RACADM)](#dell-idrac-virtual-console-status-and-power-racadm)
- [Trusting servers](#trusting-servers)
- [Health and Wake-on-LAN](#health-and-wake-on-lan)
- [Zabbix](#zabbix)
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
  - **Shift+click** selects every host between the last one you clicked and this one, as the tree shows them, and **Shift+↑/↓** extends the selection from the keyboard;
  - **Ctrl+A** in the tree selects every host the search shows, and **Esc** clears the selection;
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
- The selection, search, grouping and sort are part of the address (e.g. `/?group=site&q=env:prod&host=<id>`), so you can bookmark or share a view. Open sessions are not part of it. Closing or reloading the browser tab while sessions are open asks first.

## Keyboard shortcuts

You can use NKeeper entirely from the keyboard. Press **?** anywhere (or choose **Keyboard shortcuts** in your account menu) for the full list. Ctrl is ⌘ on a Mac.

- **Anywhere:** **Alt+Shift+H** goes to the host tree. **Alt+Shift+R**, **U** and **S** open Reports, Users and Settings.
- **Search:** **Ctrl+K** or **/** goes to the search box. **↓**, or **Enter** (which searches at once), goes on to the tree.
- **The tree** is one Tab stop:
  - **↑/↓**, **Home** and **End** move, and **←/→** close and open groups.
  - Typing a name jumps to it.
  - **Enter** selects a host, and **Delete** asks to delete it.
- **The selected host:**
  - **Ctrl+Enter** connects, like a double-click.
  - **Shift+F10** (or the Menu key) opens its menu.
  - **Ctrl+E** edits it, **Ctrl+D** clones it, and **Ctrl+S** saves the editor.
  - **Alt+Shift+N** adds a new host, and **Alt+Shift+I** imports hosts.
- **Tabs:** **Alt+PageUp/PageDown** moves to the previous or next tab (Details first), and **Alt+Shift+W** closes a session's tab. In the tab bar, **←/→** move and **Delete** closes.

Inside a terminal or remote desktop, every key goes to the remote side except **Alt+PageUp/PageDown**, **Alt+Shift+W** and **Alt+Shift+H**, so there's always a way back out. Browsers keep **Ctrl+W**, **Ctrl+T** and **Ctrl+N** for themselves (Ctrl+W would close the tab). To send them to an SSH shell, use the session's **Full screen**: in Chrome and Edge it hands those keys to the terminal, and holding **Esc** leaves full screen.

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
  - **Services** are the ways to connect: SSH, Telnet, RDP, RPC, VNC, HTTP, HTTPS, KVM, Proxmox, iDRAC, Microsoft SQL Server, Oracle, SAP ASE and Rep Server (SAP Replication Server). Those are the only types you can add. A host may still have an SFTP, FTP, WinRM, PowerShell or Other service from an older version; its details mark it *Not supported*, and editing the host says it will be removed when you save (cancel to keep it). Each has an optional port and label. The first one is the default. An **iDRAC** service also has the iDRAC's own **address**, since it isn't the server's. A **SQL Server** service has an optional **instance** name (empty for the default instance). An **Oracle** service needs its **service name**.
  - **Tools** are the checkboxes under an SSH or RPC service: which of **Connect ▸ Tools** it offers (**Files**, **Storage**, **Journal** and **SELinux** on SSH; **Event Log Viewer** on RPC). All are on by default. For SSH on a Windows host, for example, untick Journal, SELinux and Storage. This tidies the menus; it isn't a permission, as anyone who can open the service can run the same things in a terminal.
  - **Credentials** have a username, domain, label, password, SSH private key and notes. Passwords and keys are **write-only**: once saved, NKeeper only shows that one is stored. Leave the field empty when editing to keep it, or choose to clear it.
  - **Use with** says which of the host's services a credential is for: **All services** (the default), or the ones you tick. For example, `sa` only for MS SQL and `administrator` only for RDP and RPC: the Connect, Tools and Scan menus then offer each only where it belongs, and NKeeper refuses it for any other service. A credential follows its services when you reorder them, and isn't given services you add later (tick them too).

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
  - **SQL Server**: the port `sqlservr` listens on and the one set in `/var/opt/mssql/mssql.conf` (seeing SQL Server's process needs root, or `sudo` without a password; otherwise it's found by its install and configuration);
  - **Oracle**: the databases in `/etc/oratab`, the running instances (`ora_pmon_<SID>`), the listener's port, and the service names it offers (`lsnrctl status`, when the login may run it; its internal XDB, `SYS$` and GUID-named services are left out);
  - the local accounts that can log in (a real shell; root and regular users);
  - the CPU cores (`nproc`), installed memory (`lsmem`, else the kernel's total) and, on physical hardware, the serial number. Only root can read the serial number on most systems: scan as root, or as a user with passwordless sudo, to get it.

  **On the network, an internal scan is a single SSH login**, like an administrator's, so it suits places that don't allow scanning. The commands only read local state and contact no other machines. It needs Linux or another Unix with `sh`.

  **Windows hosts** are scanned over an **RPC** service instead (**Tasks ▸ Scan ▸ Internal**, *Over RPC (WMI)*), with a Windows login and its domain. It makes read-only WMI queries: the computer name, manufacturer and model, the Windows version, network adapters, listening TCP ports, **SQL Server instances** (from SQL Server's own registry settings: each instance's name, whether TCP/IP is on, and its static or dynamic port), enabled local accounts, logical processors, installed memory (the memory modules added up) and the BIOS serial number. WMI uses RPC on port 135 plus Windows' dynamic RPC ports, so the firewall must allow those from the NKeeper server, and the account must be allowed remote WMI (an administrator is). On Linux, NKeeper uses Impacket for this: the Docker image and the Linux installer include it.
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

  Internal scans offer each **SQL Server instance** they find as an MS SQL service with its instance name and port (e.g. *MS SQL · SQLEXPRESS, port 49712*), unless the host already has that instance. An instance on a dynamic port, one with TCP/IP turned off, or one installed but not running is explained in the scan's notes.

  Internal scans offer each **Oracle database** they find as an Oracle service with its service name (e.g. *Oracle · ORCLPDB1*). If the listener's services can't be read, its instances are offered by SID, and the notes say so. An external scan only sees the listener's port: its Oracle proposal has a **Service name** box, and typing a name ticks it.
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

- **Neighbours on the network** (internal scans): the scan reads the host's own neighbour (ARP/NDP) table — the machines it has recently talked to — and matches each to an NKeeper host by its **MAC address**, else its **IP address**. This fills in network segments the NKeeper server can't see without a loud scan of its own; nothing is probed, as the table is already there. Each neighbour that's already a host is summarised under **Known neighbours**. Each one that isn't can be added (administrators): tick **Add …** for the addresses you want, start unticked, with **Select all**. A new host gets the neighbour's IP and MAC address; add a name, services and logins afterwards. The host's own addresses and NKeeper's are left out, as are incomplete, loopback, link-local and multicast entries. A neighbour table only holds recent contacts and expires quickly, so it shows who's been active lately, not the whole network.

Changing a host's hostname or IP address forgets its trusted keys too, for the same reason.

## Scanning a network

**Scan network** (administrators, in the toolbar above the host list) finds every machine on a subnet and learns what it can about each one. It's the loud counterpart to reading a host's neighbour table.

> **This scan is loud.** The NKeeper server pings every address, tries about 65 TCP ports on each machine that answers, and talks to the services it finds. Firewalls, intrusion detection and prevention systems, honeypots and other security tools may treat it as an attack: they may raise alerts or block the NKeeper server. The dialog says so, and you must tick that you understand and are allowed to scan the network before **Start scan** is enabled. Every scan is recorded in the audit trail (*Network scanned*, filter **Network scans**).

- **Target:** an IPv4 subnet in CIDR form, a /20 (4,096 addresses) at most, such as `192.168.1.0/24`. The server's own networks are offered as suggestions. A /24 takes about a minute.
- **Finding machines:** an address counts as a machine when it answers a **ping**, answers (or refuses) a **TCP** connection on a common port, or resolves in **ARP** (only on the server's own network segment). A machine that drops all of those isn't found.
- **What it learns about each machine:**
  - its open TCP ports (the external scan's ports, plus ones that identify printers, NAS boxes, hypervisors, container hosts, databases and management interfaces);
  - its names: reverse DNS, its **NetBIOS** name and workgroup (Windows and Samba), and the names in its Remote Desktop and web certificates;
  - its MAC address (from ARP on the server's segment, or from its NetBIOS answer across routers) and, for well-known prefixes, the maker: VMware, Hyper-V, QEMU/KVM, Proxmox, VirtualBox, Xen, Docker, Raspberry Pi, Synology. A virtual machine's prefix marks it as one;
  - what its services say: the SSH banner, each web page's server and title (up to four web ports), certificates, and its ping TTL (a hint at the operating system family);
  - an operating system guess, from the SSH banner, or *Windows* when Remote Desktop, Microsoft RPC or Windows file sharing answers.
- **Matching:** each machine is matched to an NKeeper host by its **MAC address**, else its **IP address**, else its **hostname**. Click an address to see everything found about it.
- **Adding machines:** tick the new ones you want (or **Select all**) and **Add hosts**. Each new host gets its short hostname as its name (its IP address if there's no name, or if a host already has it), its hostname, IP and MAC address, *Virtual machine* when the MAC says so, an `os` tag, and a service for each open port NKeeper has a service for. Add logins afterwards.

The scan runs on the server, so closing the dialog doesn't stop it: reopen **Scan network** to see its progress, and **Stop scan** to end it early (what it found so far stays). Only one network scan runs at a time. A finished scan is shown again for an hour. Run from a container, NKeeper only sees MAC addresses if it uses the host's network.

## Connecting

Right-click a host in the tree, or press the Menu key or Shift+F10 on it, then choose **Connect ▸ service ▸ credential**. Each session opens as a tab next to the host details and keeps running while you switch tabs, and while you're on Reports, Users or Settings: **Hosts** brings you back to them as you left them. Sessions close when you close them, sign out, or close or reload the browser tab.

**Connect** lists the clients: SSH, Telnet, RDP, VNC, RPC, web interfaces, iDRAC and VM consoles. The tools that do more than open a client are under **Connect ▸ Tools**: **Files**, **Storage**, **Journal** and **SELinux** (on an SSH service), **Event Log Viewer** (on an RPC service), **KVM** (a hypervisor's VMs), **MS SQL**, **Oracle**, **SAP ASE** and **Replication Server**.

- **Double-click** a host to connect with its first connectable service and first suitable credential. If none fits, you're asked for a login.
- The **Connect** button in the host details opens the same menu, which is the way to connect on touch screens.
- **Enter credentials…** connects with a one-off login that is used once and never saved. Such sessions can't reconnect without typing it again.
- When a session ends normally (e.g. `exit`, or signing out of Windows), its tab closes. A session that fails stays open to show why, with **Try again** or **Reconnect**.
- Close a tab with its **×**, or middle-click it.

A credential that can't work for the service is shown greyed out with the reason, for example "No username". A credential without a stored password (or SSH key) shows **Asks for password**: choosing it asks for the password, with its username filled in, and uses it once. To stop being asked, edit the host and set the password.

**No connection is left open behind you.** Closing a session's tab closes its connection to the host. Closing the browser tab or window does too, for every session in it. If the browser just disappears (it crashed, the computer went to sleep, the network dropped), the server notices within about a minute for SSH, Telnet, RDP, VNC, RPC and VM consoles, and within about 3 minutes for web and database sessions (SQL Server, Oracle, SAP ASE, Replication Server), then closes their connections. That includes a SQL query still running. A database tab whose session the server closed stays as it is, marked *Disconnected*. The next thing you do in it (expanding the explorer, a report, running a query) connects again: a query window reconnects in the database or schema it was in. Temporary tables and uncommitted work from before are gone.

**The NKeeper server makes every connection**, not your PC. Hosts must be reachable and resolvable from the server. Your passwords and keys are decrypted only there, never sent to your browser.

## SSH

A full terminal (xterm.js) that resizes with the tab.
- **Authentication:** a credential can use a password, a private key, or both. The password doubles as the key's passphrase. Keyboard-interactive prompts are answered with the password.
- **Domain accounts:** a credential's domain is sent as `DOMAIN\user`, which is what Windows OpenSSH expects.
- **Idle sessions** stay open.
- **Full screen** (in the status bar): in Chrome and Edge, **Ctrl+W** (delete the previous word), **Ctrl+T** and **Ctrl+N** then reach the shell instead of the browser. Hold **Esc** to leave full screen.

## Telnet

For switches, routers, serial-console servers and other devices that only offer Telnet (port 23 by default). It's the same terminal as SSH:
- **Terminal:** NKeeper tells the device it's an `xterm-256color` terminal and keeps it informed of the window size, so full-screen menus fit the tab.
- **Logging in:**
  - Telnet has no login of its own: the device asks in the terminal.
  - With a saved credential, NKeeper answers the first `login:` (or `Username:`) and `Password:` prompts for you, once, just after connecting. A password-only credential answers just the password prompt.
  - **Without a login** (in the service's menu, and what double-click does when the host has no credential) connects and leaves the typing to you.
- **Not encrypted:** Telnet sends everything, passwords included, in the clear between NKeeper and the device. The status bar says **Unencrypted to host**. Your browser's connection to NKeeper is still protected. Prefer SSH wherever the device offers it.

## Files (SFTP)

Any host with an SSH service has **Connect ▸ Tools ▸ Files ▸ credential** (or **Files ▸ SSH service ▸ credential** when it has several). The files open in a tab:
- **Browsing:** the tab starts in the login's home folder. Open a folder by clicking it, go back with **Up** or the path above the list, and **Refresh** to see changes made elsewhere.
- **Sorting:** click **Name**, **Size** or **Modified** to sort by that column, and again to reverse it. Folders stay above files except by size, where they mix so you can see what's taking the space. Folders still being calculated go last.
- **Folder sizes** fill in on their own after the list appears, and you can keep working while they do. **≥** means part of the folder couldn't be read, so it's at least that big; **—** means the size couldn't be worked out. Symbolic links aren't followed, and `/proc`, `/sys` and `/dev` aren't counted.
- **Upload:** choose one or more files, up to 2 GB each. They go into the folder shown. If a file with the same name exists, NKeeper asks before replacing it.
- **Download:** files stream from the host through the NKeeper server to your browser. A folder downloads as a zip: the NKeeper server builds it in its temp folder and deletes it after the download, so a big folder takes a while to start. NKeeper checks first that the server has room, and refuses with the sizes if the folder's size plus the space an administrator keeps free (5 GB unless changed in **Settings → Limits**) is more than it has free. The check doesn't allow for compression. Anything that can't be read is left out and listed in `_nkeeper-unreadable.txt` in the zip. Symbolic links aren't included.
- **New folder**, **Rename** and **Delete**. Delete asks first, and only deletes empty folders.
- **Logins and trust** are the same as SSH: the same credentials, host key check and trusted keys. The SSH server must have its SFTP subsystem enabled (OpenSSH does by default).
- **Audit:** every upload, download, rename, delete and new folder is in the audit log, with its path and size.

## Storage (iSCSI, multipath, DRBD)

Any Linux host with an SSH service has **Connect ▸ Tools ▸ Storage ▸ credential** (or **Storage ▸ SSH service ▸ credential** when it has several). NKeeper runs the host's own tools over SSH and shows what it finds in a tab, one section for each tool that's installed:
- **iSCSI** (`iscsiadm`, from open-iscsi or iscsi-initiator-utils): the initiator's name, and every target it knows with its portal, session state and attached disks. **Discover** asks a portal (e.g. `10.0.0.5` or `10.0.0.5:3260`) for its targets (SendTargets). **Log in** and **Log out** each target, and **Rescan sessions** to see new LUNs. A SAN that redirects logins to its data addresses (Nimble, Dell EqualLogic…) is shown under the portal you logged in through, e.g. its discovery address, with the address it *connected via* beneath.
- **Multipath** (`multipath`, from multipath-tools or device-mapper-multipath): each map with its WWID, device, vendor and size, and every path's device-mapper, checker and kernel state. A map is green when all its paths are ready. **Reload maps** after changing the configuration, and **Flush** a map that's no longer used. **Rename** a map to give it a name of your own: NKeeper sets the alias in the map's block in the host's multipath configuration (or adds `/etc/multipath/conf.d/nkeeper-alias-<wwid>.conf` when it has none) and reloads the maps. If the map doesn't take the name, the configuration is put back. A rename applies to this host only, and whatever has the map open keeps working, but its old `/dev/mapper` name goes away, so change anything that refers to it (fstab, LVM filters, DRBD, VM disks).
- **DRBD** (`drbdadm` and `drbdsetup`): every configured resource, including those that are down, with this node's role, its disks' states and each peer's connection, role, replication and sync progress. Bring a resource **Up** or **Down**, make it **Primary** or **Secondary**, **Connect** or **Disconnect** its peers, or **Adjust** it to its configuration.
- **Asks first:** logging out, flushing, taking a resource down, changing its role and disconnecting it say what will happen and wait for you to confirm. The tools still refuse what would break something in use (a mounted map or a primary in use), and the tab shows their answer.
- **Root:** the tools need root. Sign in as root, or as a user who may use `sudo` without a password.
- **Logins and trust** are the same as SSH. Each change is recorded in the audit log (category *Storage*), with what it acted on.

## Event Log Viewer (Windows)

Any host with an **RPC** service has **Connect ▸ Tools ▸ Event Log Viewer ▸ credential** (or **Event Log Viewer ▸ RPC service ▸ credential** when it has several). NKeeper reads the host's event logs over WMI and shows them in a tab, newest first. It only reads; nothing on the host changes.

- **Logins:** use a saved credential with its domain, or **Enter credentials…** and type a domain account as `DOMAIN\user`. A typed login is kept in memory for as long as the tab is open, so it can refresh, and isn't saved.
- **What the login needs:** remote WMI (DCOM), as for [an internal scan](#scanning-hosts): administrators have it, and others need the *Event Log Readers* group and WMI permissions. The host's firewall must allow WMI (port 135 and DCOM's dynamic ports).
- **The Security log** needs the *Manage auditing and security log* right. Without it, Windows doesn't list the log, and the tab says so.
- **Which logs:** those Windows lists in `Win32_NTEventlogFile`, such as Application, System and Security, and logs that programs register alongside them. The newer logs under *Applications and Services Logs* in Event Viewer (for example `Microsoft-Windows-…/Operational`) can't be read over WMI.
- **Filtering:** choose a log, a time range and levels (none for all), or give a **Source** and an **Event ID** and choose **Apply**. It starts with all time, newest first. If a time range leaves nothing, **Search all time** clears it. Critical events show as errors, and verbose ones as information.
- **Reading:** click a message to see all of it, with its task category, user and computer. Each read gets up to 200 events; **Load older events** gets the next ones. **Find** only searches the events already loaded.
- **Large logs:** a filter that few events match can make Windows search much of the log. After 50 seconds the tab says the host didn't finish, so choose a shorter time range or fewer filters.
- **Jump hosts:** it can't go through one yet, so it's greyed out on hosts that have one.
- **Audit:** opening or refreshing a log is in the audit log (category *Windows event logs*), with the log and the account. What the events said isn't recorded.

## Journal (journalctl)

Any Linux host with systemd and an SSH service has **Connect ▸ Tools ▸ Journal ▸ credential**. NKeeper reads its journal with `journalctl` and shows it in a tab, newest first. It only reads.

- **Filtering:** **Priority** (that level and worse) applies as soon as you choose it. Set a **Start** and **Stop** (in your browser's time zone; leave either empty), a **Unit**, a **Program** and a **Search**, then **Apply**. It starts with no time limit, newest first. If the times leave nothing, **Search all time** clears them.
- **Search** is a regular expression on the message, run on the host. Written in lower case it ignores case; any capital makes it case-sensitive. It needs systemd 237 or later (not CentOS 7).
- **Reading:** click a message to see all of it, with its unit, PID and host. Each read gets up to 200 entries; **Load older entries** gets the next ones. **Find** only searches the entries already loaded. Messages over 4 KB aren't sent by the journal, and say so.
- **Follow** shows new entries as they arrive, on one SSH connection, so watching doesn't fill the journal with logins. It needs an empty **Stop**. A tab keeps the newest 5,000 entries.
- **The login:** root or a user who may use `sudo` without a password sees everything. So does one in the `systemd-journal` group. Others see only their own entries, and the tab says so.
- **Logins and trust** are the same as SSH. Reading and following are recorded in the audit log (category *Linux journals*), but what the entries said isn't.

## SELinux

Any Linux host with an SSH service has **Connect ▸ Tools ▸ SELinux ▸ credential**. It shows the host's SELinux mode, what it has blocked, and its policies, in one tab. It uses the host's own tools, and needs root, or a user who may use `sudo` without a password. Hosts without SELinux (usually Debian and Ubuntu) say so.

- **Blocks and audits** come from the audit log (`ausearch`), so `auditd` must be running. Set a **Start** and **Stop** (the last 24 hours to begin with), then **Apply**; **Find** searches what's shown. **Blocked** was stopped. **Logged** wasn't, because the host or that domain is permissive, but would be if enforcing. Click a target to see its contexts and the audit record. Only the newest 1,000 records are shown. What a `dontaudit` rule hides isn't logged, so it can't appear here.
- **Adding a policy from blocks:** tick what to allow, choose **Create policy from selected** and name the policy. `audit2allow` on the host writes the rules, and you read (and can edit) them before **Install**. If its comments name a boolean that would do the job, that's usually better than a new rule. Nothing is installed until you choose to.
- **Writing a policy:** **Add policy** takes a type enforcement (`.te`) source starting with `module name 1.0;`. NKeeper compiles and installs it with `checkmodule`, `semodule_package` and `semodule`, and shows any compile errors so you can fix them. `policy_module()` sources need the policy's development files, so compile those on the host.
- **Removing a policy:** **Remove** asks first, as what the policy allowed is blocked again at once. NKeeper doesn't keep the source.
- **The distribution's modules** are listed but can't be removed, or replaced by a policy with the same name, as the system depends on them.
- **Installing and removing** rebuild the host's whole policy, which can take a minute or more. Each is checked against the modules the host lists afterwards, and recorded in the audit log (category *SELinux policies*).
- **Tools on the host:** `audit2allow` is in `policycoreutils-python-utils` and `checkmodule` in `checkpolicy` on Red Hat family systems. If one is missing, the tab says which.
- **Not here:** changing the mode, booleans, file contexts and ports. Use SSH for those.

## Microsoft SQL Server

Add a **Microsoft SQL Server** service to the host for each instance. Leave **Instance** empty for the default instance (MSSQLSERVER), or type the named instance (e.g. `SQLEXPRESS`). The port is 1433 by default; a named instance without a port is looked up with the SQL Server Browser (UDP 1434). Add a **SQL login** (e.g. `sa`) as a credential with a password. Windows logins (a credential with a domain) can't be used, and are greyed out. Then **Connect ▸ Tools ▸ MS SQL ▸ instance ▸ login** opens a tab like a small SSMS:

- **Object Explorer** (left): the server, its **Databases** (system ones under *System Databases*), and in each database its **Tables**, **Views**, **Synonyms**, **Programmability** (stored procedures and functions, with their parameters) and **Security** (users, roles, schemas). A table opens to its **Columns** (`Id (PK, int, not null)`), **Keys**, **Indexes** and **Triggers**. The server's **Security** lists logins and server roles, and **Management** has the **Blocking Manager** and **Orphaned Users**. Double-click or press → to expand, F5 to refresh. Right-click (or Shift+F10) for **New Query**, **Select Top 1000 Rows** (opens and runs it in a new window), **Script as CREATE**, **Script as EXECUTE** and **Copy name**.
- **Layout**: drag the bar between the Object Explorer and the query windows, or between a query's editor and its results, to give one more room (or focus the bar and use the arrow keys; double-click it to reset). The sizes are remembered in your browser.
- **Query windows** (right): **New Query** opens another. Each has its own connection, as in SSMS, so it keeps its database, temporary tables and open transactions, and a long query in one doesn't hold up the others. **F5**, **Ctrl+Enter** or **Ctrl+E** runs the selection, or everything when nothing is selected; **Cancel** stops it. `GO` splits batches (`GO 5` runs one five times). Choose the database from the toolbar, or with `USE`.
- **Results**: as a **Grid** (one per result set; click a cell, then Ctrl+C or right-click to copy it, its row, or everything with headers) with **Messages** beside it, or as **Text**. Messages list PRINT output, "(n rows affected)" and errors as SSMS writes them (`Msg 208, Level 16, State 1, Line 5`); double-click an error to select its line. A result set shows up to 10,000 rows (50,000 per query), with the full count.
- **Blocking Manager** (in the Object Explorer, under **Management**): **Lead Blockers**, the sessions at the root of each blocking chain, with their login, host, program, database, status, open transactions, how many sessions they block directly (**Blocked**) and down the whole chain (**Blocked Volume**), and their current SQL. The sessions they block are collapsed under them: expand a lead, or use **Expand all**. A blocked session shows what it waits on and for how long. A session blocking itself is listed too, marked **SELF**, even with nothing waiting on it. That is usually its own parallel threads waiting on each other, so a busy server can show running parallel queries here; the wait type tells you which. **Right-click** a session (or press Shift+F10 or Delete on it) to **Kill** it, after confirming; its open transaction is rolled back. It refreshes every 10 seconds (or as chosen). Seeing other logins' sessions needs **VIEW SERVER STATE**, and killing them needs **ALTER ANY CONNECTION** (sysadmin has both). Kills are recorded in the audit log (category *SQL Server*).
- **Orphaned Users** (in the Object Explorer, under **Management**): database users whose login is gone, or no longer has their SID. This is what restoring a database on another server leaves behind: the user is there, but nobody can sign in as it. NKeeper checks every database you can access and lists each orphaned user with its type, default schema and the login of the same name, if there is one. To fix one:
  - **Map to** the login of its own name (`ALTER USER … WITH LOGIN`), the usual repair. **Fix all matched by name** does this for every user that has one.
  - **Map to a login…** of your choosing, of the same kind (SQL or Windows).
  - **Create its login…**: a SQL login with a password you choose and the user's own SID, so the same user in other databases restored from the same server maps to it too; or a Windows login (`FROM WINDOWS`).
  - **Drop user…**, after confirming. A user that owns schemas can't be dropped.

  **In bulk:** tick users (or use **Select: All / Without a login of their name / With a login of their name**), then **Map selected to their logins** or **Drop selected…** (after confirming). Each user is fixed on its own: the ones that can't be (e.g. a user that owns schemas) are listed with why, and the rest go ahead.

  Right-click a user (or Shift+F10) for every fix. Databases whose owner login is missing are listed below, with **Set owner…**. Users created without a login on purpose, and contained users, aren't orphans. Fixes are recorded in the audit log (category *SQL Server*), never with the password; they need permission to alter users and create logins (sysadmin, or securityadmin and db_owner).
- **Encryption and trust**: the connection is always encrypted. The server's certificate is trusted like an HTTPS server's: once, then pinned. A SQL Server without a certificate of its own makes a new self-signed one (*SSL_Self_Signed_Fallback*) every time it starts, so NKeeper asks you to trust the new one after a restart instead of refusing to connect. Give the server a real certificate to have any change refused outright.
- **Jump hosts**: SQL Server goes through the host's jump host. A named instance then needs its port set on the service (the SQL Server Browser can't be asked through the tunnel).

## Oracle

Add an **Oracle** service to the host for each database you sign in to. **Service name** is required: the database's service name (e.g. `ORCLPDB1`, `XEPDB1`) or its SID. If no service has that name, NKeeper tries it as a SID. Each pluggable database has a service name of its own. The port is 1521 by default. Add a database user (e.g. `system`) as a credential with a password; a credential with a domain can't be used. Signing in as `SYS` connects as SYSDBA. Then **Connect ▸ Tools ▸ Oracle ▸ service name ▸ login**. An internal scan finds the service names for you (see [Scanning hosts](#scanning-hosts)).

The tab works like the SQL Server one: Object Explorer, query windows, results and messages, and Blocking Manager. The differences:
- **Schemas, not databases:** the Object Explorer lists schemas (Oracle's own are under *Oracle-maintained Schemas*), and a query window's **Schema** list sets its current schema (`ALTER SESSION SET CURRENT_SCHEMA`).
- **Scripts as in SQL\*Plus:** a SQL statement ends at `;`. A PL/SQL block (`DECLARE`, `BEGIN`, `CREATE PROCEDURE`…) ends at a line holding only `/`. `EXEC proc(…)` works, and SQL\*Plus settings such as `SET SERVEROUTPUT` are skipped. `DBMS_OUTPUT` shows under Messages. A compile error lists its errors with their lines.
- **Script as CREATE** uses `DBMS_METADATA`, for tables, views, procedures, functions, packages, triggers, sequences and synonyms.
- **Blocking Manager** kills with `ALTER SYSTEM KILL SESSION 'sid,serial#' IMMEDIATE`. It needs the `ALTER SYSTEM` privilege.

**Management** also has three reports. They only read; anything that would change the database is offered as a script, which opens in a query window for you to review and run.
- **Storage:**
  - Tablespaces with their size, used, free, % used and **% of maximum size**. The maximum counts autoextend: a file's MAXSIZE, or its size when it doesn't autoextend.
  - Temporary tablespaces, and every datafile.
  - Warns at 85% of the maximum. For tablespaces past 70%, it offers `ADD DATAFILE` (beside the last file, or in its ASM disk group or the Oracle-managed file location) and `AUTOEXTEND ON` scripts.
- **Backups (RMAN):**
  - Archivelog mode, 14 days of RMAN jobs, any running now with % done, and each datafile's last backup.
  - The fast recovery area, RMAN settings changed from the defaults, and the last job's output.
  - Warns when the last database backup is over 26 hours old (with when it finished), when jobs failed, when datafiles were never backed up, and when the recovery area is 85% full.
  - RMAN can't run over a database connection, so its commands (full and incremental backups, crosscheck, delete obsolete, validate…) are shown to copy and run in `rman target /` on the server.
- **Health:**
  - Instance, uptime and size; sessions and processes against their limits.
  - Outstanding alerts, alert-log errors from the last 24 hours (Oracle 12c and later), and redo log switches per hour.
  - Invalid objects (with recompile scripts), locked or expiring accounts (with unlock scripts), and long-running operations.

The reports read the `DBA_` and `V$` views, so the login needs `SELECT_CATALOG_ROLE` or `SELECT ANY DICTIONARY`. Without it, each section shows its own error.

**Encryption:** NKeeper asks the server for Oracle's native network encryption and integrity checking, but connects without it if the server doesn't offer it. To make it required, set `SQLNET.ENCRYPTION_SERVER = required` in the server's `sqlnet.ora`. There's no certificate to trust.

## SAP ASE and Replication Server

**SAP ASE (Sybase):** add a **SAP ASE** service (port 5000 by default) and an ASE login with a password. Then **Connect ▸ Tools ▸ SAP ASE ▸ login**. Query windows split batches on `GO`, as `isql` does. The Object Explorer lists databases (system ones apart), tables, views, procedures, functions, users, groups and logins, with *Select Top 1000 Rows* and *Script as CREATE* (from `syscomments`). The Blocking Manager reads `sysprocesses` and kills with `kill`.

ASE's **Management** has **Copy Database** and four folders of tools. Reports only read; what they'd change is a script that opens in a query window.
- **Activity:**
  - **Blocking Manager.**
  - **Resources & Activity:** `sp_monitorconfig` limits close to running out (with `sp_configure` scripts to raise them), long-open transactions, top sessions by CPU, cache hit rates, engines.
  - **Error Log:** errors of severity 17 and up, and deadlocks, from the last 24 hours. This is from what the server keeps in memory; the report offers the settings that turn that on.
- **Storage & Backups:**
  - **Space:** each database's data and log (size, free, % used) and the devices, with `alter database` scripts for full ones.
  - **Backups (dumps):** each database's last full and log dump, failed or running dumps, and full logs. Full-dump times come from monitoring, so they're known since the server started, unless dump history is on.
  - **Maintenance:** each database's largest tables, and statistics older than 30 days (with `update index statistics` scripts).
- **Replication:**
  - **Replication Health:** each database's log against replication's truncation point and the oldest open transaction, and the Rep Agents.
  - **Replication Drift:** for each replicate database, the last transaction applied from each primary (when, and how long it took), `rs_ticket` history, and the exceptions log when the RSSD is on this server.
  - **Compare** (in Replication Drift): counts the rows of a database's replicated tables here and on another ASE server you have open in a tab. Estimated counts are instant; exact ones run `count(*)`.
- **Security:**
  - **Logins & Users:** locked or expired logins (with unlock scripts); users with no login, or matched to a login of another name, as after a load from another server; logins unused for 90 days.
  - **License:** each license, its status and expiry (`monLicense`, `sp_lmconfig`). Warns in a grace period or within 30 days of expiry.

**Copy Database** dumps a database here and loads it on another ASE server you have open in a tab:
1. **Choose and check:** choose the database, the target server and the copy's name, then **Check**. It checks the page size, version and character set; that the target isn't a system database or in use; and that there's room. If the target database doesn't exist, it writes a `create database … for load` on the target's roomiest devices, which you can edit.
2. **Run the steps:** create, dump, load, online, then clearing the replication truncation point if the source is replicated. Run them one by one, or **Run all** (it asks first, and stops at the first failure).
3. **Getting the dump file across:** the target's Backup Server must be able to read the dump. Either dump **through** the target's Backup Server, so the file is written on its machine, or use storage both machines share. Dumping through it needs that Backup Server in the source's `sysservers`; the list shows their network names.
4. **Long runs:** each step runs for as long as it takes, even if you close the page. Each is recorded in the audit log.
5. **Afterwards:** users map to logins by ID, so check them with **Logins & Users**.

**Replication Server:** add a **Rep Server** service (port 11752 by default) and a Replication Server login. Then **Connect ▸ Tools ▸ Replication Server ▸ login**. Its query window runs `admin` commands. **Management** has:
- **Replication Health:** what's down, the threads, queue backlog, stable devices and warm standby. Suspended connections can be **Resumed**, or resumed skipping the failed transaction (it goes to the exceptions log), after confirming. Both are audited.
- **Admin Commands:** `admin version`, `rssd_name`, `health`, `who_is_down`, `who_is_up`, `who` (and by thread: SQM, SQT, DIST, DSI, RSI), `show_connections`, `logical_status`, `disk_space`, `statistics, backlog`, route and site versions, and `config`. A command your version doesn't have shows its error; the rest still run.
- **License:** `sysadmin lmconfig`.

**Permissions:** the monitoring tables (Error Log, caches, engines, full-dump times, `monLicense`) need `mon_role` and `enable monitoring`. `sp_help_rep_agent` needs `sa_role` or `replication_role`. Copying needs permission to dump and load (`sa_role`, or `oper_role` and owning the database), and clearing the truncation point needs `sa_role` or `replication_role`. A section the login can't read shows why, and the rest of the report still loads.

**Encryption:** the password is sent encrypted. The rest of the connection is whatever the server is set up for: NKeeper doesn't use ASE's own SSL. Use a jump host or a network you trust.

## Jump hosts

A **jump host** (or bastion) is an SSH server that NKeeper signs in to first, then tunnels a host's connections through. Use it for sites, VLANs and customer networks that the NKeeper server can't reach directly.
- **Setting one:** edit the host and choose a host under **Reach through**, then which of its credentials to **sign in as** (by default, its first credential with a username and a password or key).
- **Which hosts can be jump hosts:** hosts with an SSH service that NKeeper reaches directly. There's one hop: a jump host can't have a jump host of its own, and a host that others go through can't get one.
- **What goes through it:** SSH, Telnet, files, web sessions, iDRAC (console, status and power, and RACADM), KVM consoles and internal SSH scans. The host details show **Reached through**, with a link to the jump host.
- **What doesn't yet:** RDP, VNC, RPC and the Event Log Viewer are greyed out in the Connect menu, since the RDP/VNC gateway and WMI dial hosts themselves. External scans and health checks probe from the NKeeper server, so they skip these hosts.
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
- when you close the browser tab or window it's open in, or about 3 minutes after the browser stops answering (asleep, off the network, crashed);
- after 12 hours in total;
- immediately if your account is disabled or you lose access to the host.

**Reload** reloads the page inside the tab.

If web sessions don't load from other computers, the server needs the wildcard DNS record and certificate described in [Installation](installation.md#dns-for-web-sessions). Links in a page that hard-code the host's absolute address (e.g. `https://10.0.0.5/…`) leave the session and connect directly; most web interfaces use relative links.

## Windows RPC

An **RPC** service (port 135, the endpoint mapper) is mainly for [scanning Windows hosts](#scanning-hosts) and the [Event Log Viewer](#event-log-viewer-windows). **Connect ▸ RPC** also opens a low-level tool for administrators who know DCE/RPC. NKeeper binds the interface and signs in with NTLM using the credential and its domain. You then send calls as an operation number and a request stub in hex, and see the response stub or the fault. Calls are authenticated but not signed or encrypted.

## KVM and Proxmox virtual machines (SPICE and VNC consoles)

Add a **KVM** service (port 22 by default) to a hypervisor host, with an SSH credential. The same service works for libvirt hosts and Proxmox VE nodes: NKeeper finds out which one it is. Then **Connect ▸ Tools ▸ KVM ▸ credential**:

- **The VM list:** like virt-manager, NKeeper connects over SSH, asks libvirt (`qemu:///system`) for the virtual machines, and lists each one's name, state and console type. **Refresh** updates the list.
- **The console:** **Open console** on a running VM with a SPICE or VNC display opens it in its own tab, with **Ctrl+Alt+Del**, **Type clipboard** and **Fullscreen**.
- **Type clipboard:** VM consoles share no clipboard, so this types the clipboard's text into the VM, key by key (up to 10,000 characters; **Stop typing** stops it). If the browser won't let NKeeper read the clipboard, it asks you to paste the text instead. VNC consoles type any character the VM's keyboard layout has. SPICE consoles press the keys of a US keyboard, so other characters are skipped (and you're told how many), and a VM with another layout gets that layout's characters for those keys.
- **From the VM itself:** a VM host that a scan linked to its libvirt host (see *Which VMs run where*) has **Connect ▸ KVM console ▸ login**, which opens its SPICE or VNC console in the VM's own tab. It also has **Tasks ▸ KVM ▸ action ▸ login**: Shut down, Reboot, Pause, Hibernate (a managed save: memory to disk, then off), Stop, Reset, Resume and Start, each confirmed first. A shutdown waits until the VM is off. The logins are the libvirt host's, for its KVM service, which it needs. **Migrate…** moves it to another KVM host with its storage (see [Migrating VMs with their storage](#migrating-vms-with-their-storage)). Actions are recorded in the audit log.
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
- **A VM or container:** a host that a scan linked to its Proxmox node (see *Which VMs run where*) has **Connect ▸ Proxmox console ▸ login**. The logins are the node's, for its Proxmox service. The console opens in the VM's own tab, with **Ctrl+Alt+Del**, **Type clipboard** and **Fullscreen**.
- **How it connects:** the way Proxmox's own console does. NKeeper's server signs in to the node's API, asks it for a VNC proxy (`vncproxy`), and relays the proxy's WebSocket to the tab. Any node of the cluster serves every VM, so it doesn't matter which node a VM moved to. Containers get their terminal console the same way.
- **Logins:** a Proxmox user, e.g. `root`, `root@pam` or `admin@pve`. Without a realm, the credential's **domain** is used as the realm, else `pam`. An **API token** works too: `user@realm!tokenid` as the username and the token's secret as the password. Accounts with two-factor sign-in can't be used; give NKeeper an API token instead.
- **Tasks ▸ Proxmox ▸ action ▸ login** runs a power action or migration on the VM or container:
  - **Migrate…** moves it to another node you choose, with its storage (see [Migrating VMs with their storage](#migrating-vms-with-their-storage)). A running VM can move live; a running container is restarted on the new node. Disks on the node's local storage can be copied along.
  - **Shut down** and **Reboot** ask its operating system (a VM needs ACPI or the guest agent). **Pause** freezes it in memory. **Hibernate** saves its memory to disk and stops it. **Stop** and **Reset** act at once, like the power and reset buttons, and lose anything unsaved.
  - **Resume** continues a paused one; **Start** starts a stopped or hibernated one.
  - The dialog shows its current state and asks first, then follows Proxmox's task until it ends. Closing the dialog doesn't stop the task. Containers can't hibernate or reset.
  - Starting one that's already running, or shutting down or stopping one that's already off, isn't a failure: it's already as wanted, and the dialog says so (*was already running*, *was already off*). The same goes for KVM VMs.
  - Each action is recorded in the audit log. Users with *Use* access to the VM get these too: what the Proxmox login may do is up to Proxmox.
- **Permissions:** the account needs **VM.Console** on the VM to open its console, **VM.PowerMgmt** for power actions, and **VM.Migrate** to migrate (and to see it). It doesn't need root, SSH or sudo.
- **Certificate:** the node's certificate is trusted on first use, like any HTTPS service (it's shared with the node's web sessions).
- **Reconnecting:** each console is good for one connection. **Reconnect** opens a new one.
- **The node** must be an NKeeper host you can see, with a Proxmox service. Otherwise there's no Proxmox console or Proxmox task entry.

## Migrating VMs with their storage

**Tasks ▸ Proxmox ▸ Migrate…** or **Tasks ▸ KVM ▸ Migrate…** moves a VM to another host and takes its storage along: whatever has to happen on each host for the VM's disks to be there, NKeeper does, in order, and puts back if something fails.

1. **Choose** where it goes. A KVM VM moves to another NKeeper host with a KVM service and a saved login. A Proxmox VM moves to another node of its cluster. For storage, NKeeper also needs SSH to the two nodes: it uses the NKeeper hosts named like the nodes (by name or host name) that have an SSH or KVM service and a saved login. Without them, Proxmox moves the VM on its own, as before, and NKeeper doesn't check the storage.
2. **Detect storage.** NKeeper logs in to both hosts and shows:
   - **what each disk is**, in words, traced down from its image file or device through the file system, LVM, DRBD, multipath and iSCSI: e.g. *an image file on the host's own disk*, *an image file on network storage (nfs4 at /mnt/nas)*, *an image file on a file system (ext4 at /srv/vms) on DRBD resource r0*, or *DRBD resource r1, used directly as the disk*. When nothing has to be handed over, it says so;
   - **only the options that apply** (see *Options*): no iSCSI choice without iSCSI, no DRBD choice without DRBD, no live choice for a VM that isn't running (or a reason instead, when it can't move live);
   - **what blocks the move**, e.g. a DRBD resource that isn't configured on the new host or isn't UpToDate there, tools that aren't installed there, a disk on the old host's own storage, a VM already defined on the new host;
   - **SSH between the hosts**, tested first wherever the move needs it: Proxmox moves VMs over its nodes' own SSH, and a live libvirt move runs `virsh migrate` over SSH from the old host to the new one (and back, to roll back). A failed test blocks the migration, or turns a live move into an offline one, and says why;
   - Changing an option checks again. **Change** goes back to choosing where it goes.
3. **Review steps.** Before anything happens, NKeeper lists every step it will take, in order, each naming exactly what and where: e.g. *Unmount /srv/vms (ext4 on /dev/drbd0) on kvm01*, *Log in to iSCSI target iqn… at 10.0.0.5:3260 on kvm02*, *Copy web's libvirt definition from kvm01 to kvm02 (virsh dumpxml, then virsh define), then remove it from kvm01 (virsh undefine)*. It marks the optional tidying-up steps, says from which step until which the VM is off, and what happens if a step fails. Only then can you start it. When you do, NKeeper plans again; if the steps would now be different (something changed on the hosts), it doesn't start, and asks you to review again.
4. **Migrate.** The migration runs on the NKeeper server, step by step, and goes on if you close the dialog. **If a critical step fails, every step already done is undone, newest first**: the storage goes back to the old host and the VM is started there again if it was running. If putting it back fails too, NKeeper says what's left to fix by hand. Afterwards, the VM's host in NKeeper is updated to the new one (if you may change it).

**What it does with the storage:**
- **iSCSI:** when it detects the storage, NKeeper asks each portal from the new host which targets it offers (`iscsiadm -m discovery -t sendtargets -o nonpersistent`: no login, no node records saved). A target the portal doesn't offer to the new host (e.g. the SAN's access list doesn't include its initiator), or a portal it can't reach, blocks the migration before anything stops. During the migration it logs the new host in to each target the VM's disks are on, discovering it at the portal first if the new host doesn't know it. Then it waits for the multipath maps (found by WWID, so their names may differ).
- **DRBD:** brings the resource up on the new host if it's down, and waits until it's connected and UpToDate there. A DRBD device is recognised by its device number (DRBD's major, 147), and its resource by its configured devices (`drbdadm sh-dev`), so DRBD 8.4, whose status lists no minors, works too.
- **A file system on shared storage** (e.g. ext4 or XFS on DRBD, or on an iSCSI LUN) can be mounted on only one host at a time, so **the VM is shut down**. On the old host the file system is unmounted, its LVM volume deactivated and the DRBD resource made Secondary. On the new host the resource is made Primary, the volume activated and the file system mounted at the same place. Then the VM moves and starts. Anything else on the old host using that file system must stop first, or unmounting fails and the migration is put back. A file system that **autofs** mounts (e.g. `/vmimages/<vm>` from an autofs map) is recognised under autofs's trigger; on the new host autofs mounts it once the resource is Primary there.
- **A shared block device used directly** (a DRBD device, LVM volume or multipath device per disk) can move **live**. For DRBD, the resource may be Primary on both hosts only for the move (`allow-two-primaries`, which needs protocol C), then it's Secondary on the old host again.
- **An image file on the old host's own disk** (e.g. `/var/lib/libvirt/images/vm.qcow2`, KVM) is **copied** to the same path on the new host. Only a file on the old host's own disks is copied: one on a file system on DRBD or iSCSI moves with that storage (above), never as a copy. NKeeper traces each disk with `lsblk`; when that can't follow it (e.g. ZFS), it goes by what the storage tools list instead: a multipath map (taken to be iSCSI), a DRBD device, or a disk an iSCSI session attached is shared storage, and anything else is the host's own. The disk's description says when it went by that. NKeeper first reads its format and size (`qemu-img info`), and refuses if it's an overlay of a backing file, if the file already exists on the new host (it never overwrites), or if there isn't room there.
  - **Offline**, after the VM is shut down: straight from the old host to the new one over their SSH (`rsync`, or `dd` over `ssh`) when they reach each other; otherwise NKeeper relays it over its own two SSH connections, in 64 MiB pieces (slower, but needs nothing between the hosts). Then it checks the copy's size.
  - **Live**, when the hosts reach each other over SSH and the new host has `qemu-img`: an empty image of the same format and size is created there, and `virsh migrate --live --copy-storage-all` copies the disk while the VM runs.
  - If the migration is put back, the copy on the new host is deleted. The old copy stays unless you choose to delete it.
- **Cluster and network storage** (GFS2, OCFS2, NFS, Ceph…) is reached by every host. NKeeper only makes sure a cluster file system is mounted on the new host.
- **Proxmox storage:** Proxmox activates its own iSCSI, shared LVM, Ceph, NFS and DRBD (LINSTOR) storage on the new node. NKeeper still makes sure the new node is logged in to the iSCSI targets. Proxmox copies the disks on storage that isn't marked shared and leaves the rest where they are, so NKeeper traces directory, LVM and LVM-thin storage and blocks the migration when Proxmox would get it wrong:
  - a **directory storage** on a file system NKeeper moves (e.g. on DRBD), or an **LVM storage** on DRBD or iSCSI, must be marked **shared**, so Proxmox doesn't copy its disks. **LVM-thin** on DRBD or iSCSI can't be shared, so its disks must move to other storage first;
  - **shared LVM on DRBD** needs the resource Primary on the new node too (dual-primary, `allow-two-primaries`), since Proxmox activates the disk there while the old node goes on using the volume group;
  - a storage **marked shared that is on the old node's own disk** would leave the VM without its disk on the new node.
- **Shared disks by kernel name:** a VM that uses a disk on iSCSI or multipath as `/dev/sdb`, `/dev/dm-3` or `/dev/mapper/mpatha` can't move: the same name may be another disk on the new host. Use a `/dev/disk/by-id/…` name.
- A **stopped VM's LVM volume** (not active, so without a device) is traced through its volume group's physical volumes.

**Options** (each shown only when it applies):
- **Live:** move a running VM while it runs, when its storage and the hosts allow; otherwise it's shut down for the move, and the check says why.
- **Start it on the new host** after an offline move, if it was running.
- **iSCSI on the old host afterwards:** stay logged in, or log out of the targets this VM used. NKeeper only logs out of targets nothing else on the old host uses (another volume, a mount, an open device), and removes their multipath maps first. The default is to log out on KVM hosts (often one host per volume) and to stay logged in on Proxmox nodes (usually all logged in to all; Proxmox logs its nodes in to its own iSCSI storage again anyway).
- **DRBD on the old host afterwards:** stay Secondary (it keeps replicating), or take the resource down.
- **Copy local disks:** KVM copies image files on the old host's own disk (see above), on unless you turn it off; Proxmox copies disks on the node's local storage itself, when you turn it on.
- **Delete the old copies afterwards** (KVM, when copying): an optional tidying-up step once the VM runs on the new host. Off by default.

Tidying up the old host (logging out, taking DRBD down, deleting old copies) never puts the migration back: if it fails, the migration is done with a warning, and the step says why.

**What the hosts need:** root, or sudo without a password, on both, and the tools for their storage (`iscsiadm`, `multipath`, `drbdadm`, `lvm`). libvirt moves a VM offline by defining it on the new host (`virsh dumpxml` / `define`) and removing it from the old one, so the hosts don't need SSH between them for that. The migration's start and end are in the audit log.

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
- **SQL Server** certificates are trusted the same way. The self-signed certificate SQL Server makes for itself when it has none changes at every restart, so for that one NKeeper asks again instead of refusing (see [Microsoft SQL Server](#microsoft-sql-server)).
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

## Zabbix

When an administrator has set up Zabbix (see [Administration](administration.md#zabbix)), NKeeper can show what Zabbix knows about a host. NKeeper only reads from Zabbix; it never changes anything there.

**Linking a host:** edit the host and fill in **Zabbix host ID**, or click **Find in Zabbix…** and search by name, IP address or DNS name, then pick the host. **Unlink** clears it. The ID is the number Zabbix shows in a host's address (`hostid=10084`). Administrators can also link many hosts at once from **Reports ▸ Zabbix**.

**In the host's details**, a **Zabbix** section shows:
- **Status:** whether Zabbix can reach the host's interfaces (available, unavailable, partly), whether it's monitored or in maintenance, its host groups and tags;
- **Problems:** the host's unresolved problems, with their severity and since when;
- **Latest values:** the common ones the host's templates collect, such as OS, uptime, CPU count and utilization, load, memory, swap, root disk usage, agent version and ping;
- **Inventory:** every inventory field that has a value. Turn host inventory on in Zabbix (manual or automatic) for it to have any;
- **Interfaces:** each agent, SNMP, IPMI or JMX interface and its last error.

**Open in Zabbix** opens the host's latest data in Zabbix, and **Refresh** reads it again. For a host you can manage, NKeeper also offers to **copy** the serial number, MAC address, CPU count and memory Zabbix has onto the host, when they differ. Values the host doesn't have yet are ticked; ones that would replace a value aren't.

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

**Zabbix** (administrators) compares Zabbix's hosts with NKeeper's:
- **Probable matches:** NKeeper hosts that aren't linked yet but look like a Zabbix host, matched by MAC address (from the Zabbix inventory), then IP address, then hostname (a DNS name or technical name, with or without the domain), then name. Tick them and **Link**.
- **Only in Zabbix:** tick the hosts and **Add to NKeeper**. Each new host gets Zabbix's visible name, the main interface's IP address and DNS name, the inventory's MAC address and serial number, the description and the tags (tags whose name has a colon are left out), optionally the host groups as `group` tags, and is linked to its Zabbix host. Hosts without any address are skipped, and the reason is shown.
- **Only in NKeeper:** hosts Zabbix doesn't seem to monitor.
- **Linked to a missing Zabbix host:** links to a Zabbix host that was deleted, or that the API token can't see. **Unlink** clears them.
- **Linked:** every pair, folded away.

**IP address map** is a spreadsheet of your address space:
- **Columns** are /24 subnets, one for each subnet that has at least one host (e.g. `10.0.1.x`, `192.168.1.x`, `192.168.42.x`), in numeric order. Each header shows how many of the subnet's addresses are used.
- **Rows** are the last octet, `.0` to `.255`.
- **Cells** show the name of the host at that address. Hover for its hostname, and click to open it in the workspace. An address used by two or more hosts is highlighted as a conflict.
- **Only used addresses** hides rows that are empty in every subnet. **Download CSV** saves the table, as shown, for Excel or another spreadsheet.
- Hosts with only a hostname or an IPv6 address aren't in the map. The summary line counts them.

**Insurance questionnaire** answers what insurers (and e-discovery requests) ask about servers, databases and backups, and downloads it as a Word document (**Download Word**; Word can save it as .docx or PDF) or a web page to print (**Download HTML**):
- **Servers:** how many, physical and virtual, by operating system, by what they run and by `role`, `env`, `site` and similar tags, with a table of every server (address, OS, location, CPUs, memory, serial number). **Don't count hosts matching** leaves out printers, switches and the like: workspace searches separated by commas, e.g. `role:printer, role:switch`.
- **Databases:** each SQL Server, Oracle and SAP ASE service. **Read from the server** (or **Read every database server**) signs in with one of the host's saved logins and runs read-only queries: the version, the databases (Oracle: the application schemas) with their size, creation date and files, who can change data (server administrators, database roles and grants), auditing and change history (SQL Server Audit, change tracking, temporal tables, Oracle audit policies and Flashback Data Archive, ASE auditing), and jobs that delete or purge data. Nothing is changed. Reading is always started by a person; nothing runs on a schedule. The login needs to see the server's catalog (SQL Server: VIEW ANY DEFINITION and msdb; Oracle: SELECT_CATALOG_ROLE); what it can't read is said.
- **Backups:** each database server is a repository, and you can add others (file servers, e-mail, Microsoft 365…). From the server NKeeper reads backup types and how often they ran (msdb history, RMAN, ASE dumps), schedules (SQL Server Agent and Oracle scheduler jobs), retention (clean-up jobs, the RMAN retention policy, the oldest backup still available), media and backup software, destinations (folders, network shares, cloud storage) and whether backups are indexed.
- **Your answers:** what NKeeper finds is listed under each question as *Found by NKeeper*. It never guesses what only you know (what a database is for, who manages it, documentation): those are left as blank spaces in the download, to fill in there, unless you type them on the page. Answers can be given once for every database or repository, for a server, or for one database, and the most specific one is used. **Save answers** keeps them for everyone (and moves with an instance export), so next year's renewal starts from this year's.
- **Appendix:** the download can end with every query's results, from the servers read since the page was opened.

## Your account

**Signing in.** Use your NKeeper username or email and password, or your domain account if Active Directory sign-in is set up. If the sign-in page shows **Sign in with …** (for example *Sign in with Microsoft*), you can use your organization's account instead: NKeeper sends you to its sign-in page and back. **Remember me** applies to both.

**Sign-in codes.** If an administrator turned on **Email 2FA challenge code** for your account, NKeeper emails you a 9-digit code after your password (or single sign-on). Enter it on the **Check your email** page. Spaces or dashes are fine. The code works once, for 10 minutes. **Send a new code** replaces it if the email doesn't arrive. After five wrong codes, sign in again. If no code arrives at all, ask an administrator to check the email settings.

The account menu (top right) has **Change password** and **Sign out**. Changing your password signs out your other sessions. If you sign in with your Active Directory account, change your password in Windows instead. Accounts that use single sign-on have no NKeeper password: their password is managed by the identity provider.

**Color scheme.** Choose **Color scheme** in the account menu to pick a preset, which applies right away. To make your own, choose **New color scheme**, name it and pick four colors: background, panels, text and accent. NKeeper works out the rest of the palette and shows a preview. If the text would be too hard to read against the background or panels, the scheme can't be saved until you change the colors. You can keep up to 20 of your own, and edit or delete them from the same place. Your choice follows your account to other browsers. Terminal and remote console windows stay dark in every scheme, because the programs running in them expect a dark screen.

If an administrator set a temporary password, NKeeper asks you to choose your own at your first sign-in. There's no self-service password reset; ask an administrator.

# User guide

- [The workspace](#the-workspace)
- [Hosts](#hosts)
- [Importing from mRemoteNG](#importing-from-mremoteng)
- [Scanning hosts](#scanning-hosts)
- [Connecting](#connecting)
- [SSH](#ssh)
- [RDP](#rdp)
- [VNC](#vnc)
- [Web interfaces (HTTP/HTTPS)](#web-interfaces-httphttps)
- [KVM virtual machines (SPICE and VNC consoles)](#kvm-virtual-machines-spice-and-vnc-consoles)
- [Trusting servers](#trusting-servers)
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
- **Group by** up to three levels of tag names, or the service type, e.g. *site → role*. A host appears under every value it has. Hosts without the tag collect in a "No site" group.
- **Sort** by name, address or last update. Names sort naturally, so `srv2` comes before `srv10`.
- The selection, search, grouping and sort are part of the address (e.g. `/?group=site&q=env:prod&host=<id>`), so you can bookmark or share a view. Open sessions are not part of it.

## Hosts

A host has:
- a **name**;
- a **hostname and/or IP address**. When both are set, NKeeper connects to the IP address, and shows and copies it;
- an optional **MAC address** and **description**;
- its own **tags**, **services** and **credentials**:
  - **Tags** are `name: value` pairs (the value is optional), e.g. `site: Datacenter A`, `env: production`, `role: sql`. A host can have the same tag name twice with different values. Tags are what you group, search and grant access by; hosts don't live in folders.
  - **Services** are the ways to connect: SSH, RDP, VNC, HTTP, HTTPS, KVM, and others stored for later (SSMS, Telnet, SFTP, FTP, WinRM, PowerShell, Other). Each has an optional port and label. The first one is the default.
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
  - each connection becomes a host with one service (its protocol and port) and, when it has a username or password, a credential;
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
  - the local accounts that can log in (a real shell; root and regular users).

  **On the network, an internal scan is a single SSH login**, like an administrator's, so it suits places that don't allow scanning. The commands only read local state and contact no other machines. It needs Linux or another Unix with `sh`. Windows isn't supported yet.
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
  - a **KVM** service when libvirt is installed (internal scans);
  - each local account as a **credential without a password**. Using it asks for the password; set the password in the host's editor to save it.

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

## RDP

Windows desktops through Apache Guacamole's guacd.
- **Resolution:** the remote desktop is sized to the tab, at your screen's pixel density.
- **Keyboard:** typing goes to the remote computer while the desktop has focus. **Ctrl+Alt+Del** and **Fullscreen** are in the status bar. In Chromium, full screen also captures Win and Alt+Tab.
- **Clipboard:** the text clipboard syncs both ways. Reading your clipboard needs browser permission and HTTPS (or `localhost`).
- **Sound** plays.
- **Off:** file transfer, drives, printing, microphone and recording.
- **Requirements:** RDP needs a credential with a username and password; the credential's domain is used as the Windows domain.
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

## KVM virtual machines (SPICE and VNC consoles)

Add a **KVM** service (port 22 by default) to a hypervisor host, with an SSH credential. Then **Connect ▸ KVM ▸ credential**:

- **The VM list:** like virt-manager, NKeeper connects over SSH, asks libvirt (`qemu:///system`) for the virtual machines, and lists each one's name, state and console type. **Refresh** updates the list.
- **The console:** **Open console** on a running VM with a SPICE or VNC display opens it in its own tab, with **Ctrl+Alt+Del** and **Fullscreen**.
- **Network path:** the console is tunnelled through NKeeper's SSH connection to the hypervisor, so the VM's console port can stay on the hypervisor's loopback.
- **Passwords:** the VM's console password, if set, is read from libvirt and used automatically. libvirt reveals it only to accounts with full (read-write) access to `qemu:///system`.
- **Resolution:** a SPICE display is shown at the VM's resolution, and scrolls if it's bigger than the tab. With the SPICE guest agent (`spice-vdagent`) installed in the VM, the resolution follows the tab's size. A VNC display is scaled to fit the tab.
- **One SPICE console at a time:** opening another SPICE console replaces the first. VNC consoles have no such limit.
- **On the hypervisor:** the SSH user must be able to use `qemu:///system` (usually by being in the `libvirt` group), and sshd must allow TCP forwarding (the OpenSSH default).
- **Not supported:** SPICE configured for TLS only, and displays that listen on a Unix socket instead of a port. When a VM has both a SPICE and a VNC display, NKeeper opens the one libvirt lists first.

## Trusting servers

NKeeper verifies whom it connects to, using **trust on first use**:
- **What you're asked:** the first connection to a server shows its identity. That's an SSH host key fingerprint (`SHA256:…`, as `ssh-keygen -lf` prints it), or an RDP or HTTPS certificate fingerprint. Compare it with the server's real one, then choose **Trust and connect**.
- **Where it's kept:** trusted keys are stored per host and listed in its details. SSH sessions and KVM share SSH keys.
- **If it changes:** NKeeper **refuses to connect** and says so, because a reinstalled server looks exactly like an attacker in the middle. When the change is expected (a rebuild, or a renewed certificate), an administrator or anyone with *Manage* access uses **Forget trusted keys…** in the host details, then connects again.

## Your account

The account menu (top right) has **Change password** and **Sign out**. Changing your password signs out your other sessions.

If an administrator set a temporary password, NKeeper asks you to choose your own at your first sign-in. There's no self-service password reset; ask an administrator.

# NKeeper

NKeeper is a web-based remote connection manager: a browser-based alternative to mRemoteNG for a team. Keep an inventory of hosts with their services, credentials and tags. Then open SSH, RDP, VNC, web, KVM virtual-machine console and Dell iDRAC console sessions in browser tabs. The NKeeper server makes every connection, so passwords and keys never reach the browser.

## Features

- **Host inventory** with services (SSH, RDP, VNC, HTTP/HTTPS, KVM, Proxmox, iDRAC, SQL Server, Oracle, SAP ASE, …), credentials (each usable with all of the host's services, or only the ones you choose) and Zabbix-style `name: value` tags. There are no folders: you **group by** any tag, e.g. site → role, and search by text or tags (`env:prod`). VMs **inherit** the tags of the host they run on (shown dashed, marked ↳), unless they have a tag of that name or the tag is marked not to pass down (`os` tags are, by default). **Clone** a host to add similar ones quickly, or **import** your mRemoteNG connections (add, update and/or delete, with a preview first). **Scan** a host to fill in its MAC address, OS, services and users: internally over one SSH login, or externally over the network. An internal scan also reads the host's **neighbour (ARP) table** to find nearby machines and offer the unknown ones as new hosts — filling in the network without a loud scan. Scanning a hypervisor links its VMs to the hosts they are, offers to add the ones NKeeper lacks, and offers to **unlink** hosts linked to it that are no longer there. Scanning a Proxmox node in a cluster offers to add the cluster's other nodes (with the scanned node's services) and tags the nodes `cluster: <name>`.
- **Sessions in tabs**: right-click a host, then **Connect ▸ service ▸ credential** for the clients (SSH, RDP, VNC, web…), or **Connect ▸ Tools** for Files, Storage, KVM and the databases (MS SQL, Oracle, SAP ASE, Replication Server).
  - **SSH** and **Telnet** terminals (xterm.js). Telnet can log in for you with a saved credential.
  - **Files** over SFTP on any SSH service: browse, upload, download, rename and delete.
  - **Microsoft SQL Server**, like a small SSMS: **Connect ▸ Tools ▸ MS SQL ▸ instance ▸ login**. Query windows (each its own connection) with results as grids or text and the messages beside them, GO batches and running just the selection; an **object explorer** of databases, tables, views, programmability, security and their columns, keys and indexes, with *Select Top 1000 Rows* and *Script as CREATE/EXECUTE*; a **Blocking Manager** that shows each blocking chain under its lead blocker and kills a session from its right-click menu; and **Orphaned Users**, which finds database users whose login is missing or no longer matches (as after a restore) and fixes them: map to the login, create it with the user's SID, or drop the user.
  - **Oracle** (11g and later) and **SAP ASE** (Sybase) in the same kind of tab: query windows (SQL\*Plus-style scripts and `DBMS_OUTPUT` on Oracle, `GO` batches on ASE), the object explorer with scripts, and the Blocking Manager. Read-only **Management reports** (each warning with when it happened, and scripts to fix it that you run yourself):
    - **Oracle:** Storage (tablespaces and datafiles), Backups (RMAN history, recovery area, RMAN commands to copy) and Health (limits, alerts, alert log, invalid objects, accounts).
    - **SAP ASE:** Space, Backups (dumps), Maintenance, Resources & Activity, Error Log, Logins & Users, License, Replication Health and Replication Drift (with a row-count compare against another server).
    - **Copy Database** dumps an ASE database and loads it on another server, step by step, after checking it can.
    - **SAP Replication Server:** its `admin` commands, Replication Health with resuming suspended connections, and its license.
  - **Storage** on Linux hosts over SSH: **iSCSI** targets and sessions (discover, log in and out, rescan), **multipath** maps and their paths (reload, flush) and **DRBD** resources with their peers and sync progress (up, down, primary, secondary, connect, disconnect, adjust).
  - **RDP** and **VNC** desktops through Apache Guacamole's guacd, with clipboard sync, Ctrl+Alt+Del and full screen.
  - **Web interfaces** (routers, iDRAC/iLO, NAS…) are proxied by the server and shown in the tab, or can be opened directly in a new browser tab.
  - **KVM and Proxmox virtual machines**: lists a hypervisor's VMs over SSH (like virt-manager), or a whole Proxmox VE cluster's, and opens their **SPICE or VNC consoles** in the browser, with Ctrl+Alt+Del and **Type clipboard**. A VM's own menu opens its console and runs its power actions through its hypervisor.
  - **Proxmox consoles** straight from a VM's or container's own menu, through the Proxmox API with a Proxmox login or API token, like Proxmox's own console, and **Proxmox tasks**: migrate, shut down, reboot, pause, hibernate, stop, reset, resume and start.
  - **Migrations that move storage too**, for Proxmox and libvirt VMs: NKeeper traces each disk through its file system, LVM, DRBD, multipath and iSCSI, tests the SSH between the hosts, and hands the storage over (iSCSI login, DRBD primary/secondary, unmount and mount), live where the storage allows. Each step is shown, and everything is put back if one fails.
  - **Dell iDRAC**: the HTML5 virtual console in a tab, the server's status and power buttons over Redfish, and RACADM over SSH.
- **Jump hosts**: reach hosts on networks the server can't see by tunnelling through an SSH bastion. SSH, Telnet, files, web, iDRAC, KVM, the databases and internal scans all go through it.
- **Windows scans** over RPC (WMI): hostname, model, Windows version, adapters, listening ports and local accounts.
- **Hardware inventory**: CPU cores, memory and serial numbers, filled in by scans (Linux, Windows, iDRAC), with each VM's vCPUs and memory read from its hypervisor.
- **Zabbix**: link hosts to Zabbix 7.0+ through its API with an API token, and see each host's availability, problems, latest values and inventory in its details, copying its serial number, MAC address, CPUs and memory onto the host. A report compares Zabbix with NKeeper: link probable matches (by MAC, IP, hostname or name), add the hosts only Zabbix has, and see the ones it doesn't monitor.
- **Health**: every service is checked from the server every few minutes, with status dots in the tree, a report of what's down and of TLS certificates about to expire, and **Wake-on-LAN** for hosts with a MAC address.
- **Keyboard-driven if you like**: press **?** for every shortcut.
  - **Ctrl+K** to search, type-to-find in the tree, **Ctrl+Enter** to connect, **Ctrl+E** to edit, **Ctrl+S** to save.
  - **Alt+PageUp/PageDown** between tabs, which also works from inside a terminal.
  - SSH **Full screen** sends Ctrl+W to the shell instead of closing the browser tab.
- **Audit log**: who signed in, opened which session to which host, and changed what, searchable and exportable. SSH and Telnet sessions are recorded and can be played back.
- **Reports**: an IP address map of every /24 subnet in use, and each VM host's capacity with what its VMs use. Both download as CSV.
- **Trust on first use**: SSH host keys, RDP certificates and self-signed HTTPS and SQL Server certificates are confirmed once, then pinned. A changed key or certificate is refused.
- **Moving servers**: export a whole instance into one passphrase-encrypted file and import it on a fresh NKeeper, between any install modes. Stored passwords are re-encrypted with the new server's keys.
- **Users and access**: administrators and users. Users get access to tag-based groups of hosts or to single hosts, at *Use* or *Manage* level. Changes apply immediately.
- **Sign-in your way**: local accounts, **Active Directory** (LDAPS), or **single sign-on** with an OpenID Connect provider such as Microsoft Entra ID, Okta, Google or Amazon Cognito. Accounts are created at first sign-in, and groups can decide who may sign in and who is an administrator.
- **Two-step sign-in**: per user, **Email 2FA challenge code** emails a 9-digit code at every sign-in, which must be entered before there's any access. Administrators set up the SMTP server under **Settings**.
- **Secure by default**:
  - stored secrets are encrypted and write-only;
  - sessions are server-side;
  - CSRF protection, rate limiting and a strict Content Security Policy;
  - web sessions isolated on their own origins;
  - HTTPS out of the box.
- **One-command install** on Linux or Windows, in containers or as native services.

## Install

On a new machine, clone or download this repository and run the setup script:

```bash
scripts/setup.sh                       # Linux, with Docker (it offers to install Docker)
sudo scripts/setup.sh --mode native    # Linux, as a systemd service with nginx and PostgreSQL
```

```bat
scripts\setup.cmd                      :: Windows, with Docker Desktop (double-click works too)
scripts\setup.cmd -Mode Iis            :: Windows, under IIS (as administrator)
```

The script asks for the names people will use to reach the server and for the first administrator. It then builds and starts NKeeper and prints the address and sign-in details. Running it again upgrades NKeeper and keeps your data. See **[Installation](docs/installation.md)** for the options, certificates, DNS for web sessions, upgrades and uninstalling.

For Proxmox VE or another host that wants a single container, `scripts/build-image.sh` builds NKeeper as one image instead; see [Single container image](docs/installation.md#single-container-image-proxmox-ve).

## Documentation

| Guide | Covers |
| --- | --- |
| [Installation](docs/installation.md) | Setup scripts, Docker Compose by hand, HTTPS and certificates, upgrades, running behind another proxy, uninstalling |
| [User guide](docs/user-guide.md) | The workspace, hosts and tags, cloning, scanning, connecting with SSH, RDP, VNC, web, KVM/Proxmox consoles and iDRAC, files, jump hosts, and reports |
| [Administration](docs/administration.md) | The first administrator, users and access, email and sign-in codes, Active Directory and single sign-on, backups and restores, logs, database migrations |
| [Configuration](docs/configuration.md) | Every API setting and `.env` variable, and where native installs keep their files |
| [Security](docs/security.md) | How NKeeper protects accounts, secrets, sessions and the network path |
| [Development](docs/development.md) | Running from source, repository layout, how sessions work, tests, migrations |
| [Troubleshooting](docs/troubleshooting.md) | Common problems and fixes |

## Technology

| Area | Choice |
| --- | --- |
| Frontend | React 19, TypeScript 6, Vite 8, React Router 8, CSS Modules; xterm.js, Apache Guacamole 1.6 client, spice-html5 |
| Backend | ASP.NET Core on .NET 10 (LTS); SSH.NET; YARP (web sessions); Microsoft.Data.SqlClient (SQL Server), Oracle.ManagedDataAccess.Core (Oracle), AdoNetCore.AseClient (SAP ASE, Replication Server) |
| Remote desktops | Apache Guacamole `guacd` 1.6.0 (RDP and VNC) |
| Auth | ASP.NET Core Identity, cookie authentication with a server-side session store; OpenID Connect (single sign-on); MailKit (SMTP for sign-in codes) |
| Data | PostgreSQL 17, Entity Framework Core 10 (Npgsql), committed migrations |
| Deployment | Docker Compose with nginx (HTTPS); or systemd + nginx on Linux; or IIS on Windows |
| Tests | xUnit v3 with real PostgreSQL, OpenSSH, xrdp, VNC, guacd and SQL Server in Testcontainers (Oracle and SAP ASE reports against a server you name); Vitest + Testing Library |

## Status and known limitations

NKeeper is young. It's usable and well tested, but it hasn't had an independent security review. Treat it accordingly before storing production credentials.

- **Not yet automated:** backups (see [Administration](docs/administration.md#backups-and-restores) for the manual commands), and recording RDP and VNC sessions.
- **Sign-in:** two-step sign-in is by emailed code only (no authenticator apps or security keys yet). Single sign-on is OpenID Connect only (no SAML), and signing out of NKeeper doesn't sign out of the identity provider.
- **Services** are SSH, Telnet, RDP, RPC, VNC, HTTP(S), KVM, iDRAC, SQL Server, Oracle, SAP ASE and SAP Replication Server. FTP, WinRM and PowerShell aren't supported, nor SFTP as a service of its own (files open on an SSH service): imports and scans leave them out, and ones an older version stored are removed when the host is next edited.
- **Oracle and SAP ASE connections aren't pinned like SQL Server's:** Oracle's native encryption is requested, not required; ASE and Replication Server encrypt only the password (see [Security](docs/security.md)). Replication Server support hasn't yet been tried against a live server.
- **Sessions live in the API process.** Restarting it ends open sessions, and limits are per API instance. There's no SSH port forwarding. RDP, VNC and RPC can't go through a jump host yet, and hosts behind one aren't health-checked. RDP file transfer, drives, printing and microphone are off.
- **SQL Server:** SQL logins only (no Windows/Kerberos or Entra ID logins). The query editor has line numbers but no syntax highlighting or IntelliSense. Results are capped at 10,000 rows per result set (50,000 per query), with the full row count shown. A named instance without a port is found with the SQL Server Browser, which doesn't work through a jump host. Queries aren't recorded in the audit log; killing a session is.
- **KVM:** one SPICE console at a time (VNC consoles aren't limited). SPICE configured for TLS only isn't supported. On Proxmox, VM consoles are VNC only (no SPICE or serial terminals), containers have no console, and NKeeper needs root on the node.
- **Web sessions** from other machines need a wildcard DNS record. Links that hard-code a host's absolute address leave the session.
- **Passwords:** there's no self-service password reset; an administrator resets passwords.
- **Data Protection keys** are stored in PostgreSQL next to the secrets they encrypt. Native Linux installs encrypt them with a certificate kept in `/etc/nkeeper`; in Docker and IIS installs they're unencrypted, so protect the database and its backups accordingly.
- **The IIS install mode** has not been tested on a real IIS server yet. The Docker and native Linux modes have been tested end to end.

## Licence and third-party code

NKeeper is released under the [MIT License](LICENSE). It vendors the Apache Guacamole JavaScript client (Apache License 2.0) and spice-html5 (LGPL v3), unmodified apart from what their READMEs describe. See [frontend/src/vendor/guacamole-common-js](frontend/src/vendor/guacamole-common-js/README.md) and [frontend/src/vendor/spice-html5](frontend/src/vendor/spice-html5/README.md).

# NKeeper

NKeeper is a web-based remote connection manager: a browser-based alternative to mRemoteNG for a team. Keep an inventory of hosts with their services, credentials and tags. Then open SSH, RDP, VNC, web, KVM virtual-machine console and Dell iDRAC console sessions in browser tabs. The NKeeper server makes every connection, so passwords and keys never reach the browser.

## Features

- **Host inventory** with services (SSH, RDP, VNC, HTTP/HTTPS, KVM, iDRAC, …), credentials and Zabbix-style `name: value` tags. There are no folders: you **group by** any tag, e.g. site → role, and search by text or tags (`env:prod`). **Clone** a host to add similar ones quickly, or **import** your mRemoteNG connections (add, update and/or delete, with a preview first). **Scan** a host to fill in its MAC address, OS, services and users: internally over one SSH login, or externally over the network.
- **Sessions in tabs**: right-click a host, then **Connect ▸ service ▸ credential**.
  - **SSH** terminal (xterm.js).
  - **RDP** and **VNC** desktops through Apache Guacamole's guacd, with clipboard sync, Ctrl+Alt+Del and full screen.
  - **Web interfaces** (routers, iDRAC/iLO, NAS…) are proxied by the server and shown in the tab, or can be opened directly in a new browser tab.
  - **KVM virtual machines**: lists a hypervisor's VMs over SSH (like virt-manager) and opens their **SPICE or VNC consoles** in the browser.
  - **Dell iDRAC**: the HTML5 virtual console in a tab, the server's status and power buttons over Redfish, and RACADM over SSH.
- **Windows scans** over RPC (WMI): hostname, model, Windows version, adapters, listening ports and local accounts.
- **Hardware inventory**: CPU cores, memory and serial numbers, filled in by scans (Linux, Windows, iDRAC), with each VM's vCPUs and memory read from its hypervisor.
- **Reports**: an IP address map of every /24 subnet in use, and each VM host's capacity with what its VMs use. Both download as CSV.
- **Trust on first use**: SSH host keys, RDP certificates and self-signed HTTPS certificates are confirmed once, then pinned. A changed key or certificate is refused.
- **Users and access**: administrators and users. Users get access to tag-based groups of hosts or to single hosts, at *Use* or *Manage* level. Changes apply immediately.
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

## Documentation

| Guide | Covers |
| --- | --- |
| [Installation](docs/installation.md) | Setup scripts, Docker Compose by hand, HTTPS and certificates, upgrades, running behind another proxy, uninstalling |
| [User guide](docs/user-guide.md) | The workspace, hosts and tags, cloning, scanning, connecting with SSH, RDP, VNC, web, KVM/SPICE and iDRAC, and reports |
| [Administration](docs/administration.md) | The first administrator, users and access, backups and restores, logs, database migrations |
| [Configuration](docs/configuration.md) | Every API setting and `.env` variable, and where native installs keep their files |
| [Security](docs/security.md) | How NKeeper protects accounts, secrets, sessions and the network path |
| [Development](docs/development.md) | Running from source, repository layout, how sessions work, tests, migrations |
| [Troubleshooting](docs/troubleshooting.md) | Common problems and fixes |

## Technology

| Area | Choice |
| --- | --- |
| Frontend | React 19, TypeScript 6, Vite 8, React Router 8, CSS Modules; xterm.js, Apache Guacamole 1.6 client, spice-html5 |
| Backend | ASP.NET Core on .NET 10 (LTS); SSH.NET; YARP (web sessions) |
| Remote desktops | Apache Guacamole `guacd` 1.6.0 (RDP and VNC) |
| Auth | ASP.NET Core Identity, cookie authentication with a server-side session store |
| Data | PostgreSQL 17, Entity Framework Core 10 (Npgsql), committed migrations |
| Deployment | Docker Compose with nginx (HTTPS); or systemd + nginx on Linux; or IIS on Windows |
| Tests | xUnit v3 with real PostgreSQL, OpenSSH, xrdp, VNC and guacd in Testcontainers; Vitest + Testing Library |

## Status and known limitations

NKeeper is young. It's usable and well tested, but it hasn't had an independent security review. Treat it accordingly before storing production credentials.

- **Not yet automated:** backups (see [Administration](docs/administration.md#backups-and-restores) for the manual commands), an audit-event store, two-factor sign-in and mRemoteNG import.
- **Services** are SSH, RDP, RPC, VNC, HTTP(S), KVM and iDRAC. SSMS, Telnet, SFTP, FTP, WinRM and PowerShell aren't supported: imports and scans leave them out, and ones an older version stored are removed when the host is next edited.
- **Sessions live in the API process.** Restarting it ends open sessions, and limits are per API instance. There's no SSH port forwarding, jump hosts or SFTP yet. RDP file transfer, drives, printing and microphone are off.
- **KVM:** one SPICE console at a time (VNC consoles aren't limited). SPICE configured for TLS only isn't supported.
- **Web sessions** from other machines need a wildcard DNS record. Links that hard-code a host's absolute address leave the session.
- **Passwords:** there's no self-service password reset; an administrator resets passwords.
- **Data Protection keys** are stored in PostgreSQL next to the secrets they encrypt. Protect the database and its backups accordingly.
- **The IIS install mode** has not been tested on a real IIS server yet. The Docker and native Linux modes have been tested end to end.

## Licence and third-party code

NKeeper's own licence hasn't been chosen yet: the repository has no LICENSE file, so add one before publishing or sharing the code. NKeeper vendors the Apache Guacamole JavaScript client (Apache License 2.0) and spice-html5 (LGPL v3), unmodified apart from what their READMEs describe. See [frontend/src/vendor/guacamole-common-js](frontend/src/vendor/guacamole-common-js/README.md) and [frontend/src/vendor/spice-html5](frontend/src/vendor/spice-html5/README.md).

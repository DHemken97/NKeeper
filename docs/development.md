# Development

- [Running from source](#running-from-source)
- [Repository layout](#repository-layout)
- [How sessions work](#how-sessions-work)
- [Tests](#tests)
- [Database migrations](#database-migrations)
- [Conventions](#conventions)

## Running from source

Run PostgreSQL and guacd in Docker, and the API and frontend directly on your machine.

**Requirements:** .NET 10 SDK, Node.js 20.19+ (22 or 24 LTS recommended), Docker.

**1. Start PostgreSQL and guacd**, on `127.0.0.1:5432` and `127.0.0.1:4822` only:

```bash
cp .env.example .env                  # set POSTGRES_PASSWORD
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d postgres guacd
```

**2. Configure and run the API.** Secrets go in [.NET user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets), outside the repository:

```bash
cd backend
dotnet tool restore
dotnet user-secrets --project src/NKeeper.Api set "ConnectionStrings:NKeeper" "Host=localhost;Port=5432;Database=nkeeper;Username=nkeeper;Password=<POSTGRES_PASSWORD>"

# First run: a development administrator
dotnet user-secrets --project src/NKeeper.Api set "BootstrapAdmin:Enabled" "true"
dotnet user-secrets --project src/NKeeper.Api set "BootstrapAdmin:UserName" "admin"
dotnet user-secrets --project src/NKeeper.Api set "BootstrapAdmin:Email" "admin@example.com"
dotnet user-secrets --project src/NKeeper.Api set "BootstrapAdmin:Password" "<12+ chars with upper, lower and a digit>"

dotnet run --project src/NKeeper.Api   # http://localhost:5080; migrations run automatically in Development
```

The OpenAPI document is at <http://localhost:5080/openapi/v1.json> in Development.

**3. Run the frontend:**

```bash
cd frontend
npm install
npm run dev                            # http://localhost:5173
```

Vite proxies `/api`, including WebSockets, to `http://localhost:5080`. Override that with `DEV_API_PROXY_TARGET` in `frontend/.env.local`. Development therefore uses the same single-origin model as production.

**Web sessions** need `nkw-…` host names routed to the API, which Vite doesn't do. Use the Docker stack, or open NKeeper on the API's port (5080) when testing them.

| | Development (`dotnet run`) | Installed |
| --- | --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` |
| Session cookie `Secure` | off (plain HTTP on localhost) | on |
| Migrations on start-up | on | on (configurable) |
| CORS | allows `http://localhost:5173` | none (same origin) |
| OpenAPI document | `/openapi/v1.json` | not exposed |
| Errors | developer exception page | problem details, no stack traces |
| Bootstrap admin | allowed when enabled | also requires `AllowOutsideDevelopment=true` |

## Repository layout

```
backend/
  NKeeper.slnx, Directory.Build.props   .NET 10, nullable, warnings as errors
  src/NKeeper.Api/                      the HTTP API
    Access/                             HostAccess: which hosts a user can see or manage
    Configuration/                      settings classes, service registration, health check, SPA hosting (IIS)
    Contracts/                          request/response records (never EF entities)
    Controllers/                        Auth, Hosts, Users, RemoteSessions (ssh/rdp/vnc WebSockets), WebSessions, Kvm, Idrac
    Desktop/                            RDP/VNC: guacd handshake, Guacamole protocol, instruction filter, RDP certificate probe
    Idrac/                              Redfish client; iDRAC status, power and virtual console credentials
    Inventory/                          HostManager (hosts, tags, services, credentials, cloning), validation
    Kvm/                                libvirt (virsh) and Proxmox (pvesh, qm) over SSH, SPICE and VNC consoles
    Proxmox/                            the Proxmox VE API client (consoles)
    RemoteSessions/                     shared session plumbing: WebSocket protocol, target resolution, trusted keys, limits, origin check
    Security/                           antiforgery and account-status filters
    Ssh/                                SSH terminal sessions (SSH.NET)
    Transfer/                           moving an instance: encrypted bundle format, export, import, lock
    Users/                              user administration and access grants
    Web/                                web-session proxy (YARP), session store, HTTPS certificate trust
    Zabbix/                             the Zabbix JSON-RPC client (API token), queries, and matching with NKeeper hosts
  src/NKeeper.Infrastructure/           persistence and identity
    Access/                             access-grant entity
    Identity/                           users, roles, database-backed auth sessions, bootstrap admin
    Inventory/                          Host and its owned tags, services, credentials, trusted keys; credential encryption
    Persistence/                        DbContext, entity configuration, start-up migration
    Migrations/                         EF Core migrations (committed)
  tests/NKeeper.Api.Tests/              integration and unit tests; Infrastructure/ holds the container fixtures
frontend/
  src/app/                              routes, product name
  src/components/                       layout and UI components (Button, Dialog, ContextMenu, …)
  src/features/auth, account, users     sign-in, own password, user administration
  src/features/inventory/               workspace page, host tree, search and grouping, host menu, details and editor
  src/features/zabbix/                  Zabbix settings, host field and details section, comparison report
  src/features/terminal/                session tabs: SSH terminal, RDP/VNC desktop, web session, KVM VM list, SPICE and VNC consoles, iDRAC
  src/lib/api.ts                        the single HTTP client (CSRF, errors)
  src/vendor/                           Apache Guacamole client and spice-html5 (vendored, see their READMEs)
  nginx/                                nginx snippets shared by the container and native Linux, HTTPS template, certificate script
scripts/                                setup.sh (Linux), setup.ps1 and setup.cmd (Windows)
docs/                                   these guides
docker-compose.yml                      the full stack
docker-compose.dev.yml                  exposes PostgreSQL and guacd on loopback for development
.env.example                            Compose variables
```

## How sessions work

All sessions start from a **target**: a host, one of its services, and a saved credential or a one-off login. `SessionTargetResolver` loads it through the user's access rights and decrypts the secret in memory.

- **SSH** (`/api/ssh/session`, WebSocket):
  - the first message names the target;
  - the API connects with SSH.NET, asking the browser to confirm an unknown host key;
  - terminal bytes then flow both ways, rendered by xterm.js.
  - The JSON control messages are defined in `RemoteSessions/SessionProtocol.cs`.
- **RDP and VNC** (`/api/rdp/session`, `/api/vnc/session`):
  - the API performs the guacd handshake itself, with the RDP certificate fingerprint pinned;
  - it then relays Guacamole protocol between guacd and the browser's vendored Guacamole client, passing browser instructions through an allow-list.
- **Web** (`/api/web/sessions`):
  - creates a session with its own origin and a one-time launch link;
  - `WebProxyMiddleware` then forwards requests for `nkw-<id>.<domain>` hosts with YARP, rewriting cookies and framing headers.
- **KVM** (`/api/kvm/vms`, `/api/kvm/vm`, `/api/kvm/power`, `/api/spice/sessions`, `/api/vnc/consoles`):
  - lists VMs by running `virsh` over SSH, and runs their power actions with it (`start`, `shutdown`, `reboot`, `suspend`, `managedsave`, `resume`, `destroy`, `reset`);
  - a console opens an SSH local port forward to the VM's SPICE or VNC port (virsh reports VNC as a display number, port 5900 + N);
  - spice-html5 then opens one WebSocket per SPICE channel on `/api/spice/channel/{id}`, and noVNC one on `/api/vnc/channel/{id}`, each relayed raw to that forward. VNC consoles live under `/api/vnc/` so proxies already treat them as WebSockets;
  - a Proxmox node (one with `/etc/pve/nodes`) is detected by the same listing script. Its VMs come from `pvesh get /cluster/resources`, and each VNC channel runs `qm vncproxy <vmid>` over SSH, with its stdin and stdout relayed instead of a port forward. The one-time VNC password goes in as the first line of input (`LC_PVE_TICKET`), never on a command line. A VM on another node is reached with `ssh root@<node>` from the connected node, as Proxmox does.

- **Proxmox** (`/api/proxmox/consoles`, `/api/proxmox/guest`, `/api/proxmox/power`, `/api/proxmox/migrate`, `/api/proxmox/task`):
  - `ProxmoxApi` signs in to the node's API (`/access/ticket`, or an API token header), finds the guest in `/cluster/resources`, and asks its node for `vncproxy` with `websocket=1`;
  - power actions (`/api/proxmox/power`: `status/{start,shutdown,reboot,suspend,resume,stop,reset}`, hibernate is `suspend` with `todisk=1`) and migrations (`/api/proxmox/migrate`) answer with Proxmox's task ID (UPID); `/api/proxmox/task` reads its status from the node named in it, and `/api/proxmox/guest` describes a guest and lists the nodes;
  - the console is a VNC console session whose endpoint connects to `vncwebsocket` (with the ticket cookie, through the jump host when there is one) and relays it like the other channels, on `/api/vnc/channel/{id}`. The proxy's ticket is the VNC password, as in Proxmox's own console.
- **Migrations** (`/api/migrations/plan`, `/api/migrations`, `/api/migrations/{id}`):
  - a `GuestMover` (`ProxmoxMover` through the API, `LibvirtMover` with virsh over SSH) holds the logins to both hosts and gives the planner the VM's disks (`virsh domblklist`, or the Proxmox configuration, `storage.cfg` and `pvesm path`), both hosts' storage (`StorageHost`) and SSH tests between the hosts;
  - `StorageTrace` follows each disk down with `findmnt`, `lsblk -s` and `lvs` into layers (LVM, DRBD, multipath, iSCSI, local) and a layout (filesystem, block, shared, network, managed by Proxmox, local), and finds which iSCSI LUNs something else uses;
  - `MigrationPlanner` is pure: from that and the options it makes the blockers, notes and `PlannedStep`s (offline or live), each titled with exactly what it does where. Starting plans again first, and only starts if the steps are exactly the ones the user confirmed (`confirmedSteps`);
  - `MigrationJobs` runs a plan on the server: each step is a script (or a mover call) that says whether it changed anything, and a failed critical step undoes the changed ones, newest first. Jobs belong to the user who started them and are kept for two hours;
  - tests: `MigrationPlanTests` (planner and parsers) and `VmMigrationTests`, which move a VM between two SSH containers with fake virsh, DRBD and mount tools (`tests/NKeeper.Api.Tests/Migration/`) that refuse steps in the wrong order.
- **Storage** (`/api/storage/status`, `/api/storage/action`):
  - `StorageHost` runs one script over SSH (as root, or with `sudo -n`) that prints an `@@section` for each installed tool: `iscsiadm -m session -P 3` and `-m node`, `multipath -ll`, and `drbdadm sh-resources` with `drbdsetup status --json` (or its text output, for older utilities). A tool that isn't installed is null in the answer;
  - an action re-reads the state first and acts only on what the host reported (a known target and portal, an existing map, a configured resource); discovery's portal must be an address. Values are also checked against strict formats and single-quoted in the command line. It then answers with the state afterwards, and records `storage.action` in the audit log;
  - tests run the real SSH path against fake `iscsiadm`, `multipath`, `drbdadm` and `drbdsetup` scripts (`tests/NKeeper.Api.Tests/Storage/`), which keep their state in files.
- **iDRAC** (`/api/idrac/overview`, `/api/idrac/power`, `/api/idrac/console`):
  - an iDRAC service has its own address (`HostService.Address`), which `SessionTargetResolver` connects to instead of the host's;
  - status and power are Redfish over HTTPS (`RedfishClient`, HTTP Basic, the certificate pinned as for web sessions), finding the system and manager from the service root;
  - the console calls Dell's `DelliDRACCardService.GetKVMSession` for one-time credentials, then opens a web session to the iDRAC whose launch link lands once on `/console?username=…&tempUsername=…&tempPassword=…`, as Dell's launch scripts do. The HTML5 console's own WebSockets go through the web-session proxy;
  - RACADM is an ordinary SSH session started on the iDRAC service: the resolver turns it into SSH to port 22 of the iDRAC's address;
  - identities trusted for the iDRAC (its certificate, its SSH host key) are kept under the `Idrac` protocol, apart from the host's own, and are forgotten when the iDRAC's address changes.

- **Zabbix** (`/api/admin/zabbix`, `/api/zabbix/status`, `/api/hosts/{id}/zabbix`, `/api/zabbix/hosts`, `/api/zabbix/comparison`, `/api/zabbix/links`, `/api/zabbix/import`):
  - `ZabbixClient` posts JSON-RPC to `<frontend>/api_jsonrpc.php` with `Authorization: Bearer <token>`; `apiinfo.version` is sent without it, as Zabbix requires. A self-signed certificate is pinned by the fingerprint saved with the settings;
  - `ZabbixQueries` reads `host.get` (interfaces, inventory, tags, host groups), `hostinterface.get` (searching by address), `item.get` (a fixed list of well-known keys) and `problem.get`. Zabbix sends numbers as strings and an empty array for a disabled inventory;
  - `ZabbixMatcher` is pure: a stored `Host.ZabbixHostId` links for certain, then MAC, IP, hostname and name find probable matches;
  - tests: `ZabbixTests` against `ZabbixServerFixture`, an in-process fake Zabbix 7.0 API over HTTPS.

Trusted keys are kept per host, port and protocol (`TrustedKeyStore`). Session lifetimes, per-user limits and "end everything for this user" live in `SessionLimiter`, `WebSessionStore` and `ConsoleSessionStore`.

## Tests

```bash
cd backend && dotnet test             # API: integration tests against real servers
cd frontend && npm test                # Vitest (jsdom)
cd frontend && npm run lint            # oxlint
cd frontend && npm run build           # type-check and production build
```

**Backend tests use real servers, not substitutes**, started with Testcontainers (Docker required):

| Fixture | Used for |
| --- | --- |
| PostgreSQL 17 | Every database test. Each test gets its own migrated database, so the migrations are tested on PostgreSQL. Or set `NKEEPER_TEST_POSTGRES` to a server connection string whose role can `CREATE DATABASE`. |
| OpenSSH server | SSH sessions (password, key, one-off, host key trust and change), access checks, and KVM with a fake `virsh` and an echo server standing in for the SPICE and VNC displays. A second one is a fake Proxmox node (fake `pvesh`, `qm` and cluster `ssh`, used through `sudo`) |
| xrdp, x11vnc and guacd | RDP and VNC end to end, including certificate trust and the instruction filter |
| An in-process HTTP/HTTPS server | Web sessions: proxying, cookies, framing, certificates, isolation |
| An in-process fake iDRAC (Redfish over HTTPS) | iDRAC status, power, console credentials and launch, certificate trust |
| An in-process fake OpenID Connect provider | Single sign-on through ASP.NET Core's real handler: signed id_tokens, state, nonce and PKCE, account creation and refusals, groups |
| An in-process fake Zabbix 7.0 API (JSON-RPC over HTTPS) | Zabbix settings and certificate trust, host overview, search, comparison, linking and adding hosts |
| A fake SMTP transport | Email settings, test emails and emailed sign-in codes (it keeps the messages instead of delivering them) |

Without Docker (and without `NKEEPER_TEST_POSTGRES`), these tests are reported as **Skipped** with the reason, never as passed.

Frontend tests render the real app with mocked HTTP and fake WebSocket, xterm, Guacamole, SPICE and noVNC clients. They cover:
- sign-in and routing, single sign-on and emailed sign-in codes;
- the workspace (tree, search, grouping, editing, cloning);
- the host menus;
- every session type's UI;
- user administration and settings.

## Database migrations

Migrations live in `backend/src/NKeeper.Infrastructure/Migrations`. The schema is never created with `EnsureCreated()`. From `backend/`, after `dotnet tool restore`:

```bash
# After changing the model
dotnet ef migrations add <Name> --project src/NKeeper.Infrastructure --startup-project src/NKeeper.Api --output-dir Migrations

# Apply to the database in your user-secrets connection string
dotnet ef database update --project src/NKeeper.Infrastructure --startup-project src/NKeeper.Api

# Does the model need a migration?
dotnet ef migrations has-pending-model-changes --project src/NKeeper.Infrastructure --startup-project src/NKeeper.Api

# Idempotent SQL script for reviewed production rollouts
dotnet ef migrations script --idempotent --project src/NKeeper.Infrastructure --startup-project src/NKeeper.Api -o migrate.sql
```

## Conventions

- The product name is **NKeeper** everywhere.
- **API:**
  - controllers return contract records, never entities;
  - errors are problem details with a `code` for the client to act on;
  - session failures never answer `401`, which would sign the user out.
- **Secrets:** nothing that returns data may include one, and tests assert that.
- **Frontend:**
  - one HTTP client (`lib/api.ts`);
  - CSS Modules with the design tokens in `styles/global.css`;
  - no UI framework;
  - heavy session code is lazy-loaded.
- **Shell scripts** are POSIX `sh` where they run in the container (`frontend/nginx/*.sh`). `scripts/setup.sh` is bash, and `scripts/setup.ps1` targets Windows PowerShell 5.1.

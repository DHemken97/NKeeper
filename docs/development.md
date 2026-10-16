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
    Kvm/                                libvirt over SSH (virsh), SPICE and VNC console tunnels
    RemoteSessions/                     shared session plumbing: WebSocket protocol, target resolution, trusted keys, limits, origin check
    Security/                           antiforgery and account-status filters
    Ssh/                                SSH terminal sessions (SSH.NET)
    Users/                              user administration and access grants
    Web/                                web-session proxy (YARP), session store, HTTPS certificate trust
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
- **KVM** (`/api/kvm/vms`, `/api/spice/sessions`, `/api/vnc/consoles`):
  - lists VMs by running `virsh` over SSH;
  - a console opens an SSH local port forward to the VM's SPICE or VNC port (virsh reports VNC as a display number, port 5900 + N);
  - spice-html5 then opens one WebSocket per SPICE channel on `/api/spice/channel/{id}`, and noVNC one on `/api/vnc/channel/{id}`, each relayed raw to that forward. VNC consoles live under `/api/vnc/` so proxies already treat them as WebSockets.

- **iDRAC** (`/api/idrac/overview`, `/api/idrac/power`, `/api/idrac/console`):
  - an iDRAC service has its own address (`HostService.Address`), which `SessionTargetResolver` connects to instead of the host's;
  - status and power are Redfish over HTTPS (`RedfishClient`, HTTP Basic, the certificate pinned as for web sessions), finding the system and manager from the service root;
  - the console calls Dell's `DelliDRACCardService.GetKVMSession` for one-time credentials, then opens a web session to the iDRAC whose launch link lands once on `/console?username=…&tempUsername=…&tempPassword=…`, as Dell's launch scripts do. The HTML5 console's own WebSockets go through the web-session proxy;
  - RACADM is an ordinary SSH session started on the iDRAC service: the resolver turns it into SSH to port 22 of the iDRAC's address;
  - identities trusted for the iDRAC (its certificate, its SSH host key) are kept under the `Idrac` protocol, apart from the host's own, and are forgotten when the iDRAC's address changes.

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
| OpenSSH server | SSH sessions (password, key, one-off, host key trust and change), access checks, and KVM with a fake `virsh` and an echo server standing in for the SPICE and VNC displays |
| xrdp, x11vnc and guacd | RDP and VNC end to end, including certificate trust and the instruction filter |
| An in-process HTTP/HTTPS server | Web sessions: proxying, cookies, framing, certificates, isolation |
| An in-process fake iDRAC (Redfish over HTTPS) | iDRAC status, power, console credentials and launch, certificate trust |

Without Docker (and without `NKEEPER_TEST_POSTGRES`), these tests are reported as **Skipped** with the reason, never as passed.

Frontend tests render the real app with mocked HTTP and fake WebSocket, xterm, Guacamole, SPICE and noVNC clients. They cover:
- sign-in and routing;
- the workspace (tree, search, grouping, editing, cloning);
- the host menus;
- every session type's UI;
- user administration.

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

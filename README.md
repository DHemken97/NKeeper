# NKeeper

NKeeper is a free, open-source, web-based remote connection manager — a browser-based alternative to mRemoteNG for managing and sharing RDP, SSH and VNC connections across a team.

**Current status:** foundation only. This release contains the application skeleton, a real authentication system (login, session, logout) and a placeholder dashboard. Connection management, workspaces, permissions and remote sessions are not implemented yet.

## Contents

- [Technology](#technology)
- [Repository layout](#repository-layout)
- [Quick start with Docker Compose](#quick-start-with-docker-compose)
- [Local development](#local-development)
- [Creating the first administrator](#creating-the-first-administrator)
- [Host inventory](#host-inventory)
- [SSH sessions](#ssh-sessions)
- [RDP sessions](#rdp-sessions)
- [Users and access](#users-and-access)
- [Database migrations](#database-migrations)
- [Configuration reference](#configuration-reference)
- [Testing](#testing)
- [Security model](#security-model)
- [Running in production (HTTPS)](#running-in-production-https)
- [Troubleshooting](#troubleshooting)
- [Known limitations](#known-limitations)

## Technology

| Area | Choice |
| --- | --- |
| Frontend | React 19, TypeScript 6, Vite 8, React Router 8, CSS Modules (no UI framework) |
| Backend | ASP.NET Core Web API on .NET 10 (LTS), C# |
| Auth | ASP.NET Core Identity, cookie authentication with server-side session store |
| Data | PostgreSQL 17, Entity Framework Core 10 (Npgsql), committed migrations |
| API docs | Built-in OpenAPI document (`/openapi/v1.json`, Development only) |
| Tests | xUnit v3 + `WebApplicationFactory` + Testcontainers/PostgreSQL; Vitest + Testing Library |
| Deployment | Docker, Docker Compose, nginx (unprivileged) |

## Repository layout

```
backend/
  NKeeper.slnx
  Directory.Build.props          shared settings: net10.0, nullable, warnings as errors
  dotnet-tools.json              pins dotnet-ef for migrations
  src/
    NKeeper.Api/                 HTTP layer
      Controllers/               AuthController (/api/auth/*), HostsController (/api/hosts), SshController (/api/ssh/session)
      Inventory/                 HostManager, address validation, error mapping
      Ssh/                       terminal WebSocket protocol, SSH session bridge, origin check, session limit
      Contracts/                 request/response DTOs (never EF entities)
      Configuration/             options classes, service registration, health check
      Security/                  antiforgery validation filter
      Program.cs
    NKeeper.Infrastructure/      persistence and identity
      Identity/                  ApplicationUser, credential validation, DB ticket store, bootstrap options
      Inventory/                 Host and its owned HostTag, HostService, HostCredential, HostSshKey; secret protector
      Persistence/               NKeeperDbContext, entity configuration, startup initializer
      Migrations/                EF Core migrations (committed)
  tests/
    NKeeper.Api.Tests/           integration tests (real PostgreSQL) + unit tests
frontend/
  src/
    app/                         App, routes, product name config
    components/layout/           AuthLayout, AppLayout, FullPageStatus
    components/ui/               Button, TextField, PasswordField, TextArea, Checkbox, Badge, Alert, Spinner, Logo, Dialog, ContextMenu
    features/auth/               api/, components/, pages/, types/, context
    features/inventory/          workspace page, host tree, context menu, details and editor; search and grouping logic
    features/terminal/           xterm.js SSH terminal (lazy-loaded) and the one-off credentials prompt
    lib/api.ts                   the single HTTP client
    styles/global.css            design tokens
  nginx/                         nginx config used by the Docker image
docker-compose.yml               full stack
docker-compose.dev.yml           override: exposes PostgreSQL on 127.0.0.1 for host development
.env.example                     every configurable variable, documented
```

The spec suggested a separate `NKeeper.Application` project. It was intentionally left out for now: the only business logic is credential validation, which is inseparable from ASP.NET Core Identity. Add an Application project when features with real domain logic (workspaces, permissions) arrive.

## Quick start with Docker Compose

Requirements: Docker with Compose v2.

```bash
cp .env.example .env
# Edit .env: set POSTGRES_PASSWORD (letters and digits, e.g. `openssl rand -hex 24`)
# and, for the first run only, the NKEEPER_BOOTSTRAP_ADMIN_* values (see below).
docker compose up -d --build
```

Open <http://localhost:8080>.

What happens:

- **postgres** stores data in the named volume `nkeeper_postgres-data`. It has no published port.
- **backend** waits for PostgreSQL to be healthy, applies migrations (`NKEEPER_MIGRATE_ON_STARTUP=true`) and serves the API on the internal network only.
- **frontend** (nginx) serves the app on `127.0.0.1:8080` and proxies `/api/*` to `http://backend:8080`. The browser only ever talks to this one origin, so it never needs to resolve Docker service names.

Useful commands:

```bash
docker compose logs -f backend        # API logs
docker compose down                   # stop (data is kept)
docker compose down -v                # stop and DELETE the database volume
```

## Local development

Run PostgreSQL in Docker and the API and frontend directly on your machine.

Requirements: .NET 10 SDK, Node.js 20.19+ (22 or 24 LTS recommended), Docker.

**1. Start PostgreSQL** (bound to `127.0.0.1:5432` only):

```bash
cp .env.example .env                  # set POSTGRES_PASSWORD
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d postgres
```

**2. Configure and run the API** — secrets go in [.NET user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets), which live outside the repository:

```bash
cd backend
dotnet tool restore
dotnet user-secrets --project src/NKeeper.Api set "ConnectionStrings:NKeeper" "Host=localhost;Port=5432;Database=nkeeper;Username=nkeeper;Password=<POSTGRES_PASSWORD>"

# Optional, first run: create a development administrator (see next section)
dotnet user-secrets --project src/NKeeper.Api set "BootstrapAdmin:Enabled" "true"
dotnet user-secrets --project src/NKeeper.Api set "BootstrapAdmin:UserName" "admin"
dotnet user-secrets --project src/NKeeper.Api set "BootstrapAdmin:Email" "admin@example.com"
dotnet user-secrets --project src/NKeeper.Api set "BootstrapAdmin:Password" "<a password of 12+ chars with upper, lower and a digit>"

dotnet run --project src/NKeeper.Api   # http://localhost:5080, migrations applied automatically in Development
```

The OpenAPI document is at <http://localhost:5080/openapi/v1.json> in Development.

**3. Run the frontend:**

```bash
cd frontend
npm install
npm run dev                            # http://localhost:5173
```

Vite proxies `/api` to `http://localhost:5080` (override with `DEV_API_PROXY_TARGET` in `frontend/.env.local`), so development uses the same single-origin model as Docker.

To call the API cross-origin instead, set `VITE_API_BASE_URL=http://localhost:5080` in `frontend/.env.local`. `http://localhost:5173` is already in the Development CORS allow-list (`Cors:AllowedOrigins`).

### Local development differences

| | Development (`dotnet run`) | Docker / Production |
| --- | --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` |
| Session cookie `Secure` flag | off (plain HTTP on localhost) | on |
| Migrations on startup | on | on by default (`NKEEPER_MIGRATE_ON_STARTUP`) |
| CORS | allows `http://localhost:5173` | none (same origin) |
| OpenAPI document | `/openapi/v1.json` | not exposed |
| Error details | developer exception page | RFC 7807 problem details, no stack traces |
| Bootstrap admin | allowed when enabled | additionally requires `AllowOutsideDevelopment=true` |

## Creating the first administrator

There is no public registration. The first administrator is created by a guarded bootstrap step that runs at API startup:

- **Disabled by default.** Nothing happens unless `BootstrapAdmin:Enabled=true`.
- **Development only by default.** In any other environment it is skipped with a warning unless `BootstrapAdmin:AllowOutsideDevelopment=true` is also set.
- **Credentials come from configuration** (user secrets or environment variables). No password is hard-coded anywhere.
- **Create-only.** If a user with that username or email already exists, it is left untouched — the bootstrap never resets a password, so leaving it enabled is harmless.
- The account gets the `Administrator` role. The password must meet the Identity policy: at least 12 characters with upper case, lower case and a digit. Startup fails with a clear message if it doesn't.

**Docker:** in `.env`, set

```
NKEEPER_BOOTSTRAP_ADMIN_ENABLED=true
NKEEPER_BOOTSTRAP_ADMIN_ALLOW_OUTSIDE_DEVELOPMENT=true
NKEEPER_BOOTSTRAP_ADMIN_USERNAME=admin
NKEEPER_BOOTSTRAP_ADMIN_EMAIL=admin@example.com
NKEEPER_BOOTSTRAP_ADMIN_PASSWORD=<strong password>
```

run `docker compose up -d`, sign in, then set `NKEEPER_BOOTSTRAP_ADMIN_ENABLED=false`, delete the password line and run `docker compose up -d` again.

## Host inventory

After sign-in, NKeeper is a single-page workspace in the spirit of mRemoteNG: a host tree on the left, the selected host's details (or editor) on the right. On phones the two panes show one at a time.

- **Hosts** have a name, a hostname and/or IP address, an optional MAC address and description, and own their **tags**, **services** and **credentials**:
  - **Tags** are Zabbix-style `name: value` pairs (value optional), e.g. `site: Datacenter A`, `env: production`, `role: sql`. A host can carry the same name twice with different values.
  - **Services** are the ways to connect (SSH, RDP, VNC, SSMS, Telnet, SFTP, FTP, HTTP(S), WinRM, PowerShell, Other), each with an optional port and label. The first one is the default.
  - **Credentials** (username, domain, label, password, SSH private key, notes) belong to one host. Secrets are write-only: they are encrypted with ASP.NET Core Data Protection before storage and never returned by the API (responses only say `hasPassword` / `hasPrivateKey`). On update, send a credential's `id` to keep it; omitting a secret keeps the stored value, `clearPassword` / `clearPrivateKey` removes it, and credentials left out of the request are deleted.
- **Hosts are not placed in folders.** The tree is a view: **group by** up to three levels of tag names or service (e.g. site → role). A host appears under every value it has; hosts without the tag collect in a "No site" group.
- **Search** matches every term against names, addresses, MAC, description, tags, services and accounts. `env:prod` matches a tag by name and value, `site:"Datacenter A"` quotes values with spaces, and `env:` means "has an env tag". Clicking a tag on a host filters by it.
- **Sort** by name, address or last update (names sort case-insensitively and numerically, so `srv2` comes before `srv10`).
- Selection, search, grouping and sort live in the URL (`/?group=site&group=role&q=env:prod&host=<id>`), so views can be bookmarked and shared.

`GET /api/hosts` returns the whole inventory and the browser searches, groups and sorts it locally, which keeps the tree instant for inventories of hundreds to a few thousand hosts. Updates use optimistic concurrency: `PUT /api/hosts/{id}` sends back the `version` it read, and a stale version returns `409` with `"code": "concurrency_conflict"`. Connecting is described in [SSH sessions](#ssh-sessions).

## SSH sessions

Right-click a host in the tree (or press the Menu key / Shift+F10 on it) and choose **Connect ▸ SSH service ▸ credential**. Each session opens as a tab next to the host details, like mRemoteNG, and keeps running while you switch tabs. When the remote shell ends (e.g. `exit`), its tab closes by itself; a tab that ends with an error stays open to show why and offer Reconnect. You can also:

- **Double-click** a host to connect with its first SSH service and first usable credential (or be asked for one).
- Use the **Connect** button in the host details, the way to connect on touch screens.
- Choose **Enter credentials…** for a one-off login. It is used for that session only and never saved, so such sessions cannot be reconnected without typing it again.

Services other than SSH are listed but marked "Coming soon". Credentials without a username, or without a password or key, are shown disabled.

**How it works.** The browser opens a WebSocket to `GET /api/ssh/session` and sends the host, the service's index and a credential id; the API looks them up, decrypts the secret in memory and opens the SSH connection with [SSH.NET](https://github.com/sshnet/SSH.NET). Terminal bytes then flow both ways over the socket, and the browser renders them with [xterm.js](https://xtermjs.org/). Passwords and SSH keys never reach the browser. A credential's password doubles as the passphrase of its private key; password logins also answer keyboard-interactive prompts. A domain on a credential is sent as `DOMAIN\user` (as Windows OpenSSH expects). The message protocol is documented in `backend/src/NKeeper.Api/Ssh/SshProtocol.cs`.

**Host keys (trust on first use).** The first connection to a host and port shows the server's key fingerprint (`SHA256:…`, as printed by `ssh-keygen -lf`) and asks you to trust it. Trusted keys are stored per host and listed in the host details. If a server later presents a different key, NKeeper **refuses to connect** and says so: a reinstalled server looks exactly like a man-in-the-middle. When the change is expected, use **Forget trusted keys…** in the host details and connect again. Changing a host's hostname or IP also forgets its keys.

The API connects from wherever it runs: in Docker that is the backend container, so hosts must be reachable (and resolvable) from it. Idle sessions stay open: the API pings the browser every 30 seconds and nginx allows up to an hour of silence on `/api/ssh/`.

## RDP sessions

RDP services connect the same way (**Connect ▸ RDP ▸ credential**), through Apache Guacamole's `guacd` (the `guacd` Compose service, never published). Before connecting, the API reads the server's TLS certificate and asks you to trust its SHA-256 fingerprint on first use, then pins it in guacd; a changed certificate is refused (use **Forget trusted keys…** after an expected renewal). Each RDP service has a **Security** setting: Automatic (NLA when offered, otherwise TLS; the default), NLA only, or TLS only. Legacy RDP security without TLS is never used. RDP needs a credential with a password; the credential's domain is used as the Windows domain.

In the session: keyboard input goes to the remote desktop while it has focus; **Ctrl+Alt+Del** and **Fullscreen** (which in Chromium also captures Win/Alt+Tab) are in the status bar; the text clipboard syncs both ways (reading your clipboard needs browser permission and a secure origin such as `localhost` or HTTPS); sound plays. File transfer, drives, printing, microphone and recording are off. The browser client is the official Apache Guacamole 1.6.0 release, vendored and verified (see `frontend/src/vendor/guacamole-common-js/README.md`).

## Users and access

Administrators manage accounts under **Users** in the top bar; everyone can change their own password from the account menu (top right).

- **Roles.** *Administrators* see and manage every host and manage users. *Users* see only what they've been granted.
- **Access grants** give a user a **group** of hosts — a tag, e.g. `site: Datacenter A`, or just `site` for any value — or **individual hosts**, at one of two levels: **Use** (see and connect) or **Manage** (also edit and delete). Group grants follow the tags, so newly tagged hosts are included automatically. The editor shows how many hosts each grant covers. Only administrators add hosts.
- **New users** get a password from the administrator (there's a generator) and, by default, must choose their own at first sign-in. Users sign in with their username or email, so email is required and unique.
- **Changes apply immediately.** Accounts are re-checked on every request. Disabling, deleting, promoting or demoting a user, or resetting their password, signs them out everywhere, including open SSH and RDP sessions. Changing your own password signs out your other sessions. Grant changes apply to the user's next action (open sessions continue).
- **No lock-outs.** Administrators can't demote, disable or delete themselves, and NKeeper keeps at least one active administrator.

Everything is enforced by the API: hosts a user can't see answer `404` (their existence isn't revealed), hosts they can see but not manage answer `403`, and SSH/RDP sessions resolve hosts through the same rules. Account changes are logged with who made them.

## Database migrations

Migrations live in `backend/src/NKeeper.Infrastructure/Migrations` and are committed. The schema is never created with `EnsureCreated()`. Run these from `backend/` after `dotnet tool restore`:

```bash
# Create a migration after changing the model
dotnet ef migrations add <Name> --project src/NKeeper.Infrastructure --startup-project src/NKeeper.Api --output-dir Migrations

# Apply pending migrations to the database in your user-secrets connection string
dotnet ef database update --project src/NKeeper.Infrastructure --startup-project src/NKeeper.Api

# Check whether the model has changes that still need a migration
dotnet ef migrations has-pending-model-changes --project src/NKeeper.Infrastructure --startup-project src/NKeeper.Api

# Produce an idempotent SQL script for reviewed/production deployments
dotnet ef migrations script --idempotent --project src/NKeeper.Infrastructure --startup-project src/NKeeper.Api -o migrate.sql
```

`Database:MigrateOnStartup=true` (the Development and Docker default) applies pending migrations when the API starts. For controlled production rollouts, set `NKEEPER_MIGRATE_ON_STARTUP=false` and apply the idempotent script (or an [EF migration bundle](https://learn.microsoft.com/ef/core/managing-schemas/migrations/applying#bundles)) as a deployment step.

## Configuration reference

The API reads `appsettings.json`, `appsettings.{Environment}.json`, user secrets (Development) and environment variables (`Section__Key`). Every section is validated at startup; invalid values stop the API with a descriptive error.

| Setting (environment variable) | Default | Purpose |
| --- | --- | --- |
| `ConnectionStrings__NKeeper` | — (**required**) | PostgreSQL connection string |
| `Database__MigrateOnStartup` | `false` (`true` in Development and Compose) | Apply migrations at startup |
| `Authentication__CookieName` | `NKeeper.Session` | Session cookie name |
| `Authentication__SessionTimeout` | `08:00:00` | Sliding lifetime of a normal sign-in |
| `Authentication__RememberMeDuration` | `14.00:00:00` | Sliding lifetime with "Remember me" |
| `Authentication__RequireSecureCookies` | `true` (`false` in Development) | Mark the session cookie `Secure` |
| `Cors__AllowedOrigins__0`, `__1`, … | none (`http://localhost:5173` in Development) | Exact origins allowed to call the API with credentials |
| `RateLimiting__Login__PermitLimit` | `10` | Login attempts per client IP per window |
| `RateLimiting__Login__Window` | `00:01:00` | Rate-limit window |
| `ForwardedHeaders__Enabled` | `false` (`true` in Compose) | Trust `X-Forwarded-For/-Proto` from a reverse proxy |
| `ForwardedHeaders__KnownNetworks__0`, … | empty (any proxy) | CIDR ranges of trusted proxies |
| `ForwardedHeaders__ForwardLimit` | `1` | Proxy hops to unwind (2 with an extra TLS proxy in front of nginx) |
| `BootstrapAdmin__*` | disabled | See [Creating the first administrator](#creating-the-first-administrator) |

Compose-level variables (`POSTGRES_*`, `NKEEPER_*`, `VITE_API_BASE_URL`) are documented in [.env.example](.env.example).

Frontend build-time variable: `VITE_API_BASE_URL` — leave empty for same-origin (recommended). The product name and tagline are in [frontend/src/app/config.ts](frontend/src/app/config.ts).

## Testing

```bash
# Backend
cd backend
dotnet test

# Frontend
cd frontend
npm test            # Vitest (jsdom)
npm run lint        # oxlint
npm run build       # type-check + production build
```

**Backend integration tests need a real PostgreSQL server.** They are deliberately not run against an in-memory or SQLite substitute, so they also prove the migrations work on PostgreSQL. Each test gets its own freshly migrated database. The test suite finds a server in this order:

1. `NKEEPER_TEST_POSTGRES` — a server-level connection string whose role can `CREATE DATABASE`, e.g.
   `NKEEPER_TEST_POSTGRES="Host=localhost;Port=5432;Username=postgres;Password=..." dotnet test`.
   Test databases are named `nkeeper_test_<guid>` and dropped afterwards.
2. Otherwise **Testcontainers** starts a disposable `postgres:17-alpine` container (requires a running Docker daemon).
3. If neither is available, the database tests are reported as **Skipped** (with the reason) — not passed. Only the pure unit tests run.

SSH session tests start a real OpenSSH server (`lscr.io/linuxserver/openssh-server`) with Testcontainers and drive the WebSocket protocol end to end: password, private-key and one-off logins, host key trust, rejection and change detection, terminal resize, and refusal of other hosts' credentials, cross-site origins and anonymous clients. Without Docker they are skipped.

Coverage: valid login (username and email), invalid login with identical generic errors for wrong password and unknown user, validation errors, missing CSRF token, account lockout, rate limiting, `/api/auth/me` with and without a session (and that it exposes only safe fields), logout including replay of a captured cookie, idempotent logout, bootstrap-admin never resetting a password, production cookie settings over plain HTTP, readiness probe, and configuration validation. Frontend tests cover login form validation, accessibility attributes, loading state, error messages (401, 429, network), show/hide password, the API client's CSRF and error handling, and session-driven routing (signed out → login, signed in → workspace, sign out, server unreachable → retry).

## Security model

- **Passwords** are hashed by ASP.NET Core Identity (PBKDF2). Plaintext passwords are never stored or logged; failed sign-ins are logged by user ID only.
- **Generic login errors.** Wrong password, unknown user and locked-out account all return the same `401` response, and unknown users still incur a password-hash computation so timing does not reveal valid accounts.
- **Brute-force protection:** per-IP rate limiting on `/api/auth/login` (`429` with `Retry-After`) plus Identity lockout (5 failures → 15 minutes).
- **Server-managed sessions.** The authentication cookie is `HttpOnly`, `SameSite=Strict`, `Secure` outside Development, and contains only an encrypted session key. The session itself is stored in the `AuthSessions` table, so **logout deletes it server-side** — a copied cookie stops working immediately. Nothing authentication-related is stored in `localStorage` or `sessionStorage`.
- **CSRF:** every non-GET request requires an antiforgery token (`GET /api/auth/antiforgery`) sent as the `X-XSRF-TOKEN` header, validated against an HttpOnly antiforgery cookie. Tokens are bound to the user, so the client fetches a fresh one after sign-in/out (handled in `lib/api.ts`). Combined with `SameSite=Strict` this covers login CSRF as well.
- **Authorization by default:** a fallback policy requires an authenticated user on every endpoint; public endpoints opt out explicitly.
- **CORS** is off unless origins are listed explicitly; credentials are only allowed for those exact origins.
- **Errors** are RFC 7807 problem details with a `traceId`; stack traces are never returned outside Development.
- **Data Protection keys** (which encrypt cookies, tokens and stored credential secrets) are stored in PostgreSQL so sessions survive container restarts.
- **Credential secrets** are encrypted before they reach the database and are never sent back to clients. SSH sessions decrypt them on the server only.
- **SSH sessions** can only target inventory hosts, their SSH services and their own credentials (or one-off credentials sent in the first WebSocket message, never in the URL). Unknown host keys must be confirmed by the user, and changed keys are refused. Every session is logged with user, host, credential id and duration (never secrets). Each user can hold at most 20 concurrent sessions.
- **WebSocket hijacking:** browsers attach cookies to cross-site WebSocket handshakes and CORS does not apply to them, so the API only accepts terminal handshakes whose `Origin` is NKeeper itself (or a configured `Cors:AllowedOrigins` entry).
- **Content Security Policy** (set by nginx) allows scripts only from NKeeper itself. Styles additionally allow inline `<style>`, because xterm.js injects its styles at runtime.
- **Containers** run as non-root; PostgreSQL and the API publish no host ports; credentials are passed at runtime, never baked into images.
- **Stable identifiers:** users are keyed by GUID. Future workspace permissions should reference `AspNetUsers.Id` and stay separate from authentication.

## Running in production (HTTPS)

NKeeper must be served over HTTPS in production. The frontend container speaks plain HTTP on port 8080; put a TLS-terminating reverse proxy (Caddy, Traefik, nginx, a load balancer…) in front of it:

1. Keep `NKEEPER_HTTP_BIND=127.0.0.1` (or a private interface) so only the proxy can reach the container.
2. Have the proxy send `X-Forwarded-Proto: https` and `X-Forwarded-For`. The bundled nginx passes an existing `X-Forwarded-Proto` through.
3. Set `ForwardedHeaders__ForwardLimit=2` on the backend so rate limiting sees the real client IP through both proxies.
4. Keep `NKEEPER_REQUIRE_SECURE_COOKIES=true`.

**Plain HTTP:** browsers accept `Secure` cookies on `http://localhost`, so the default Compose setup works locally. On any other host over plain HTTP (e.g. `http://192.168.1.10:8080`) the browser drops the session cookie and sign-in appears to fail. Use HTTPS; `NKEEPER_REQUIRE_SECURE_COOKIES=false` exists only for throwaway test environments.

## Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| `required variable POSTGRES_PASSWORD is missing a value` | Create `.env` from `.env.example` and set `POSTGRES_PASSWORD`. |
| API exits with `ConnectionStrings:NKeeper must be configured` | Set the connection string (user secrets locally, `.env` in Docker). |
| API exits with `Failed to create the bootstrap administrator: Passwords must…` | The bootstrap password doesn't meet the policy (12+ chars, upper, lower, digit). |
| `password authentication failed` after changing `POSTGRES_PASSWORD` | PostgreSQL only reads it when the volume is first created. Change the password inside PostgreSQL, or `docker compose down -v` to start over (deletes data). |
| Sign-in succeeds but you are immediately sent back to the login page | The browser rejected the `Secure` session cookie because the site isn't HTTPS (and isn't `localhost`). See [Running in production](#running-in-production-https). |
| `400` with `"code": "csrf_token_invalid"` from a custom client | Fetch `GET /api/auth/antiforgery` (keeping cookies) and send the token as `X-XSRF-TOKEN`; fetch a new one after login/logout. |
| `429 Too many attempts` while testing | Login rate limit; wait for `Retry-After` seconds or raise `RateLimiting__Login__PermitLimit` in development. |
| Correct password rejected after several failures | Identity lockout (15 minutes after 5 failures). |
| Login page says it can't reach the server | API not running, or the Vite proxy target is wrong (`DEV_API_PROXY_TARGET`), or the backend container failed — check `docker compose logs backend`. |
| Database tests show as Skipped | Neither Docker nor `NKEEPER_TEST_POSTGRES` is available; see [Testing](#testing). |
| `dotnet ef` not found | Run `dotnet tool restore` in `backend/`. |
| SSH: "Couldn't connect … the name could not be resolved" / "host is unreachable" | The API (the backend container in Docker) makes the connection. The host must be resolvable and reachable from there, not just from your PC. |
| SSH: "Host key changed" | The server presented a different key than the one trusted. Verify with the server's administrator; if expected, use **Forget trusted keys…** in the host details. |
| SSH: "Authentication failed" with the right password | The server may only accept keys, or the account may be locked. Try the same login with `ssh` from the backend's network. |
| Terminal tab says "Couldn't reach the NKeeper server" | The WebSocket was blocked. Behind your own reverse proxy, forward `Upgrade`/`Connection` headers and the original `Host` for `/api/ssh/` (see `frontend/nginx/default.conf`). |

## Known limitations

- SSH and RDP sessions are implemented; VNC and the other services are listed as "Coming soon". RDP one-off logins have no domain field yet (use a saved credential for domain accounts). Workspaces, user groups (teams), an audit-event store, SignalR collaboration and mRemoteNG import are not implemented yet. 
- Terminal sessions live in the API process: restarting the backend ends them, and the per-user session limit is per instance. SSH agent forwarding, port forwarding, jump hosts and SFTP are not supported yet.
- No self-service "forgot password" (an administrator resets it); the link shows a placeholder.
- Data Protection keys are stored unencrypted in PostgreSQL (ASP.NET Core logs a warning about this), next to the credential secrets they encrypt. Protect them with a certificate or key vault (`ProtectKeysWith…`) before production use, and back them up: losing the keys makes stored passwords and SSH keys unrecoverable.
- Expired sessions are purged opportunistically when new sessions are created; there is no background cleanup job yet.
- Rate limiting is in-memory and per API instance.

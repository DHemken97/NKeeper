# NKeeper

NKeeper is a free, open-source, web-based remote connection manager — a browser-based alternative to mRemoteNG for managing and sharing RDP, SSH and VNC connections across a team.

**Current status:** foundation only. This release contains the application skeleton, a real authentication system (login, session, logout) and a placeholder dashboard. Connection management, workspaces, permissions and remote sessions are not implemented yet.

## Contents

- [Technology](#technology)
- [Repository layout](#repository-layout)
- [Quick start with Docker Compose](#quick-start-with-docker-compose)
- [Local development](#local-development)
- [Creating the first administrator](#creating-the-first-administrator)
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
      Controllers/               AuthController (/api/auth/*)
      Contracts/                 request/response DTOs (never EF entities)
      Configuration/             options classes, service registration, health check
      Security/                  antiforgery validation filter
      Program.cs
    NKeeper.Infrastructure/      persistence and identity
      Identity/                  ApplicationUser, credential validation, DB ticket store, bootstrap options
      Persistence/               NKeeperDbContext, entity configuration, startup initializer
      Migrations/                EF Core migrations (committed)
  tests/
    NKeeper.Api.Tests/           integration tests (real PostgreSQL) + unit tests
frontend/
  src/
    app/                         App, routes, product name config
    components/layout/           AuthLayout, AppLayout, FullPageStatus
    components/ui/               Button, TextField, PasswordField, Checkbox, Alert, Spinner, Logo
    features/auth/               api/, components/, pages/, types/, context
    features/dashboard/          placeholder dashboard
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

Coverage: valid login (username and email), invalid login with identical generic errors for wrong password and unknown user, validation errors, missing CSRF token, account lockout, rate limiting, `/api/auth/me` with and without a session (and that it exposes only safe fields), logout including replay of a captured cookie, idempotent logout, bootstrap-admin never resetting a password, production cookie settings over plain HTTP, readiness probe, and configuration validation. Frontend tests cover login form validation, accessibility attributes, loading state, error messages (401, 429, network), show/hide password, the API client's CSRF and error handling, and session-driven routing (signed out → login, signed in → dashboard, sign out, server unreachable → retry).

## Security model

- **Passwords** are hashed by ASP.NET Core Identity (PBKDF2). Plaintext passwords are never stored or logged; failed sign-ins are logged by user ID only.
- **Generic login errors.** Wrong password, unknown user and locked-out account all return the same `401` response, and unknown users still incur a password-hash computation so timing does not reveal valid accounts.
- **Brute-force protection:** per-IP rate limiting on `/api/auth/login` (`429` with `Retry-After`) plus Identity lockout (5 failures → 15 minutes).
- **Server-managed sessions.** The authentication cookie is `HttpOnly`, `SameSite=Strict`, `Secure` outside Development, and contains only an encrypted session key. The session itself is stored in the `AuthSessions` table, so **logout deletes it server-side** — a copied cookie stops working immediately. Nothing authentication-related is stored in `localStorage` or `sessionStorage`.
- **CSRF:** every non-GET request requires an antiforgery token (`GET /api/auth/antiforgery`) sent as the `X-XSRF-TOKEN` header, validated against an HttpOnly antiforgery cookie. Tokens are bound to the user, so the client fetches a fresh one after sign-in/out (handled in `lib/api.ts`). Combined with `SameSite=Strict` this covers login CSRF as well.
- **Authorization by default:** a fallback policy requires an authenticated user on every endpoint; public endpoints opt out explicitly.
- **CORS** is off unless origins are listed explicitly; credentials are only allowed for those exact origins.
- **Errors** are RFC 7807 problem details with a `traceId`; stack traces are never returned outside Development.
- **Data Protection keys** (which encrypt cookies and tokens) are stored in PostgreSQL so sessions survive container restarts.
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

## Known limitations

- Remote connections, workspaces, permissions, credential vault, audit events, SignalR collaboration and mRemoteNG import are not implemented yet.
- No self-service password reset or user administration UI; the "Forgot password?" link shows a placeholder.
- Data Protection keys are stored unencrypted in PostgreSQL (ASP.NET Core logs a warning about this). Protect them with a certificate or key vault (`ProtectKeysWith…`) before production use.
- Expired sessions are purged opportunistically when new sessions are created; there is no background cleanup job yet.
- Rate limiting is in-memory and per API instance.

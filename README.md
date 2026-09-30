# NKeeper

A browser-based sysadmin gateway: one web app for connection management (replacing mRemoteNG) and, through modules, everything else.

- **Backend:** ASP.NET Core (.NET 10), EF Core (SQLite in dev, PostgreSQL in production), SignalR
- **Frontend:** React 19 + TypeScript + Vite + Tailwind CSS v4
- **Auth:** pluggable drivers (local database, Active Directory/LDAP, Linux PAM) plus TOTP MFA

## Layout

```
NKeeper.slnx
Directory.Build.props             shared .NET settings (net10.0, nullable, ...)
src/
  NKeeper.Core/                   contracts + entities, no infrastructure
    Interfaces/IAuthenticationDriver.cs
    Entities/                     User, MfaCredential, Datacenter, Node, ConnectionProfile
    Modules/INKeeperModule.cs     the backend module contract
    Security/                     role and policy names
  NKeeper.Infrastructure/
    AuthDrivers/                  LocalDb, ActiveDirectory, LinuxPam
    Services/                     TotpService, JwtTokenService, UserProvisioningService
    Data/AppDbContext.cs
    Options/                      strongly typed configuration
    DependencyInjection.cs
  NKeeper.Server/                 host: Program.cs, AuthController, module loader
  Modules/
    NKeeper.Modules.Inventory/    datacenters, nodes, connection profiles
  nkeeper.client/                 React SPA
    src/modules/                  client modules + registry.ts
```

## First run

```powershell
# 1. JWT signing key (already done on this machine; repeat on a new clone)
cd src/NKeeper.Server
dotnet user-secrets set "Authentication:Jwt:SigningKey" "<random string of 32+ characters>"

# 2. Client packages
cd ../nkeeper.client
npm install

# 3. Run. The SPA proxy starts Vite automatically.
cd ../NKeeper.Server
dotnet run --launch-profile https
```

Open https://localhost:54742. On first start the server creates a local `admin` account and prints a random password **once** in the console. You can also set `Bootstrap:AdminPassword` before the first run. After signing in, go to **Account & security** to enroll an authenticator app.

Visual Studio: open `NKeeper.slnx` and press F5. `NKeeper.Server` is the startup project.

### Bootstrap commands (for reference: how this solution was created)

```powershell
dotnet new classlib -n NKeeper.Core -o src/NKeeper.Core
dotnet new classlib -n NKeeper.Infrastructure -o src/NKeeper.Infrastructure
dotnet new classlib -n NKeeper.Modules.Inventory -o src/Modules/NKeeper.Modules.Inventory
# (NKeeper.Server + nkeeper.client came from the Visual Studio "React and ASP.NET Core" template)

dotnet add src/NKeeper.Infrastructure package Microsoft.EntityFrameworkCore.Sqlite
dotnet add src/NKeeper.Infrastructure package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add src/NKeeper.Infrastructure package System.DirectoryServices.Protocols
dotnet add src/NKeeper.Infrastructure package Microsoft.IdentityModel.JsonWebTokens
dotnet add src/NKeeper.Server package Microsoft.EntityFrameworkCore.Design
dotnet add src/NKeeper.Server package Microsoft.AspNetCore.Authentication.JwtBearer

cd src/nkeeper.client
npm install react-router @microsoft/signalr qrcode.react
npm install -D typescript typescript-eslint tailwindcss @tailwindcss/vite @types/node
```

## Configuration

All settings are in `src/NKeeper.Server/appsettings.json`. Secrets go in user-secrets (dev) or environment variables (prod), e.g. `Authentication__Jwt__SigningKey`.

| Setting | Purpose |
|---|---|
| `Database:Provider` | `Sqlite` or `Postgres` |
| `ConnectionStrings:Default` | e.g. `Host=db;Database=nkeeper;Username=nkeeper;Password=...` |
| `Database:DataProtectionKeysPath` | **Set in production.** Persistent key directory; TOTP secrets are encrypted with these keys. |
| `Authentication:ActiveDirectory:*` | `Enabled`, `Server`, `Domain`, optional `RequiredGroupDn` / `AdminGroupDn`. LDAPS by default. |
| `Authentication:LinuxPam:*` | `Enabled`, `ServiceName` (→ `/etc/pam.d/<name>`). Linux hosts only. |
| `Modules:<id>:Enabled` | Switch a module off without removing it. |

## Sign-in flow

1. `POST /api/auth/login` `{ username, password, driver }`
   - no MFA enrolled → `{ status: "Authenticated", accessToken, ... }`
   - MFA enrolled → `{ status: "MfaRequired", mfaToken }` (5-minute token with a separate audience; it can't be used as a session)
2. `POST /api/auth/mfa/verify-totp` `{ mfaToken, code }` → session token

Codes can't be replayed (the last used time step is stored and checked under a concurrency token). Failed password and MFA attempts both count toward lockout. Anonymous auth endpoints are rate-limited per IP.

## Adding a module

A feature (SSH terminal, RDP gateway, DNS, patching, certificates, ...) is a backend module plus a client module that share the same `id`.

**Backend:**

1. `dotnet new classlib -n NKeeper.Modules.Ssh -o src/Modules/NKeeper.Modules.Ssh`. Add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` and a reference to `NKeeper.Core` (and `NKeeper.Infrastructure` if you need `AppDbContext`).
2. Implement `INKeeperModule` (see `InventoryModule.cs`). Endpoints are mounted at `/api/{id}` and require a session. Use `.RequireAuthorization(NKeeperPolicies.Admin)` for admin-only routes. Map SignalR hubs at `hubs/{name}` inside the group.
3. Reference the project from `NKeeper.Server` and add `new SshModule()` to `AddNKeeperModules(...)` in `Program.cs`.

**Client:**

1. Create `src/modules/ssh/index.ts` exporting a `ClientModule` (see `modules/inventory/index.ts`), with `serverModule: true`.
2. Add it to `src/modules/registry.ts`.
3. To make inventory's **Connect** button work for a protocol, add a `connectionHandlers` entry: `{ protocol: 'Ssh', label: 'Open terminal', route: (id) => '/ssh/' + id }`.
4. For real-time traffic, use `createHubConnection('ssh', 'terminal')` from `lib/hub.ts`.

## Not built yet

- **Database migrations.** The schema is created with `EnsureCreated`. Before the first production deployment, switch to EF migrations (one migrations assembly per provider) and set `Database:AutoCreate` to `false`.
- **FIDO2/WebAuthn.** The `MfaCredential` columns exist; there's no ceremony code yet.
- **OpenID Connect.** The enum value is reserved. OIDC is redirect-based, so it will need its own endpoints rather than an `IAuthenticationDriver`.
- **MFA policy.** No "require MFA for everyone" setting; there's also no self-service removal or admin reset of credentials.
- **Sessions.** No refresh tokens or server-side revocation. Sessions are 8-hour JWTs.
- **Credential vault** for connection profiles.
- **Audit log.**
- **RBAC** beyond admin/user.
- **Reverse proxy.** Behind one, add `ForwardedHeaders` so rate limiting sees real client IPs.

# Configuration

- [API settings](#api-settings)
- [Docker Compose variables](#docker-compose-variables)
- [Where native installs keep things](#where-native-installs-keep-things)
- [Frontend build settings](#frontend-build-settings)

The setup scripts write all of this for you. Change settings by re-running the script with options where one exists. Otherwise edit the files below and restart NKeeper.

## API settings

The API reads `appsettings.json`, `appsettings.{Environment}.json`, user secrets (Development only) and environment variables. Environment variables use `Section__Key`; arrays use `__0`, `__1`, …. Every section is validated at start-up, and an invalid value stops the API with a message saying what's wrong.

| Setting (environment variable) | Default | Purpose |
| --- | --- | --- |
| `ConnectionStrings__NKeeper` | — (**required**) | PostgreSQL connection string |
| `Database__MigrateOnStartup` | `false` (`true` in Development and in every setup-script install) | Apply pending migrations at start-up |
| `Authentication__CookieName` | `NKeeper.Session` | Session cookie name |
| `Authentication__SessionTimeout` | `08:00:00` | Sliding lifetime of a sign-in |
| `Authentication__RememberMeDuration` | `14.00:00:00` | Sliding lifetime with "Remember me" |
| `Authentication__RequireSecureCookies` | `true` (`false` in Development) | Mark the session cookie `Secure`. Keep `true` outside development. |
| `Cors__AllowedOrigins__0`, … | none (`http://localhost:5173` in Development) | Exact origins allowed to call the API with credentials, and to open session WebSockets |
| `RateLimiting__Login__PermitLimit` | `10` | Sign-in attempts per client IP per window |
| `RateLimiting__Login__Window` | `00:01:00` | Rate-limit window |
| `ForwardedHeaders__Enabled` | `false` (`true` in Compose and native Linux) | Trust `X-Forwarded-For`/`-Proto` from a reverse proxy |
| `ForwardedHeaders__KnownNetworks__0`, … | empty (any proxy) | CIDR ranges of trusted proxies |
| `ForwardedHeaders__ForwardLimit` | `1` | Proxy hops to unwind (`2` with another TLS proxy in front of the bundled nginx) |
| `Guacd__Host` / `Guacd__Port` | `localhost` / `4822` | Where guacd listens (`guacd` in Compose; `127.0.0.1` in native installs) |
| `WebProxy__BaseDomain` | empty = `web.<the name NKeeper is opened with>` | Parent domain of web-session origins (`nkw-<id>.<domain>`). Must have a wildcard DNS record. |
| `WebProxy__IdleTimeout` | `00:30:00` | A web session ends after this long without requests |
| `WebProxy__MaxLifetime` | `12:00:00` | A web session ends this long after it started |
| `BootstrapAdmin__Enabled`, `__AllowOutsideDevelopment`, `__UserName`, `__Email`, `__Password` | disabled | First administrator; see [Administration](administration.md#the-first-administrator) |
| `ActiveDirectory__Enabled` | `false` | Let users sign in with their domain account; see [Administration](administration.md#active-directory-sign-in) |
| `ActiveDirectory__Domain` | — (**required** when enabled) | NetBIOS domain name, e.g. `CORP`. Accepted as `CORP\user` at sign-in, and sent as the domain of RDP sessions that use the user's sign-in. |
| `ActiveDirectory__DnsDomain` | — (**required** when enabled) | DNS domain name, e.g. `corp.example.com`. Sign-in binds as `user@DnsDomain`. |
| `ActiveDirectory__Server` / `__Port` | the DNS domain / `636` (LDAPS) or `389` | Domain controller to query |
| `ActiveDirectory__Transport` | `Ldaps` | `Ldaps`, `StartTls`, or `None` (clear text; refused unless `ActiveDirectory__AllowInsecureTransport` is `true`) |
| `ActiveDirectory__SearchBase` | from the DNS domain (`DC=corp,DC=example,DC=com`) | Where user accounts are looked up |
| `ActiveDirectory__RequiredGroup` | empty (any enabled domain user) | Distinguished name of the group allowed to sign in. Nested membership counts. |
| `ActiveDirectory__AdministratorsGroup` | empty (roles are assigned in NKeeper) | Distinguished name of a group whose members become NKeeper administrators. It's checked at every sign-in. |
| `ActiveDirectory__PassCredentialsToRemoteSessions` | `true` | Keep the user's domain password, encrypted, with their NKeeper session, so RDP can log in as them (**My sign-in**) |
| `ActiveDirectory__Timeout` | `00:00:10` | LDAP timeout |
| `ASPNETCORE_ENVIRONMENT` | `Production` in every install | `Development` enables the OpenAPI document and the developer error page |
| `ASPNETCORE_URLS` / `ASPNETCORE_HTTP_PORTS` | `8080` in the container; `http://127.0.0.1:5080` in native Linux | Where the API listens (behind nginx) |
| `Logging__LogLevel__Default` | `Information` | Log level (`Microsoft.AspNetCore` is `Warning`) |

Fixed limits per user: 20 SSH/RDP/VNC sessions, 20 web sessions and 10 VM consoles. VM consoles close after 5 minutes with no open channel, and after 12 hours at most.

## Docker Compose variables

These go in `.env` in the repository folder. [.env.example](../.env.example) lists them all with comments.

| Variable | Default | Purpose |
| --- | --- | --- |
| `POSTGRES_DB` / `POSTGRES_USER` | `nkeeper` | Database and login names |
| `POSTGRES_PASSWORD` | — (**required**) | Database password, letters and digits. PostgreSQL only reads it when its volume is first created. |
| `POSTGRES_DEV_PORT` / `GUACD_DEV_PORT` | `5432` / `4822` | Loopback ports for local development (`docker-compose.dev.yml`) |
| `NKEEPER_HTTP_BIND` / `NKEEPER_HTTP_PORT` | `127.0.0.1` / `8080` | Plain-HTTP port of the app, for local use or an outer proxy |
| `NKEEPER_HTTPS_BIND` / `NKEEPER_HTTPS_PORT` | `127.0.0.1` / `8443` (setup script: `0.0.0.0` / `443`) | HTTPS |
| `NKEEPER_HTTP_REDIRECT_PORT` | `8081` (setup script: `80`) | Plain HTTP that redirects to HTTPS (same bind address as HTTPS) |
| `NKEEPER_SERVER_NAMES` | `localhost` | Space-separated names and IPs clients use. The private-CA certificate covers them and `*.web.<name>`. |
| `NKEEPER_WEB_BASE_DOMAIN` | empty | `WebProxy__BaseDomain`, which the certificate also covers |
| `NKEEPER_MIGRATE_ON_STARTUP` | `true` | `Database__MigrateOnStartup` |
| `NKEEPER_REQUIRE_SECURE_COOKIES` | `true` | `Authentication__RequireSecureCookies` |
| `NKEEPER_BOOTSTRAP_ADMIN_*` | disabled | First administrator |
| `VITE_API_BASE_URL` | empty | Frontend build setting (see below) |

Other API settings can be added to the `backend` service's `environment` in a `docker-compose.override.yml`, which is git-ignored and read automatically.

## Where native installs keep things

| | Native Linux | IIS (Windows) |
| --- | --- | --- |
| Application | `/opt/nkeeper/api`, `/opt/nkeeper/frontend` | `C:\inetpub\nkeeper` (frontend in `wwwroot`) |
| Settings and secrets | `/etc/nkeeper/nkeeper.env` (root only; systemd `EnvironmentFile`) | `C:\inetpub\nkeeper\web.config` (administrators and the app pool only) |
| Setup state | the same file | `C:\ProgramData\NKeeper\state.json` (administrators only) |
| Service | `nkeeper.service` (systemd), user `nkeeper` | IIS site **NKeeper**, app pool **NKeeper**; redirect site **NKeeper HTTP redirect** |
| Web server config | `/etc/nginx/conf.d/nkeeper.conf`, `nkeeper-tls.conf`, `/etc/nginx/snippets/nkeeper/` | IIS bindings |
| Certificates | `/etc/nginx/nkeeper-tls/` (`ca.crt`, `server.crt`, or your `custom.crt`) | `LocalMachine\My` (and the CA in `LocalMachine\Root`) |
| guacd | container `nkeeper-guacd` (Docker) or service `nkeeper-guacd` (Podman), `127.0.0.1:4822` | container `nkeeper-guacd` in Docker Desktop |
| Build cache | `/var/cache/nkeeper-build` | `C:\ProgramData\NKeeper\build` |

After editing a settings file: `sudo systemctl restart nkeeper` (Linux), or recycle the **NKeeper** app pool (IIS). On Linux, re-running the setup script rewrites the settings it manages and keeps the rest. Under IIS it regenerates `web.config` completely, so re-apply manual additions afterwards.

## Frontend build settings

- `VITE_API_BASE_URL` (build time): leave empty so the browser calls `/api` on the same origin, which is recommended and what every install does. Set it only to call the API cross-origin in development.
- `DEV_API_PROXY_TARGET` (`frontend/.env.local`, development only): where the Vite dev server proxies `/api`. The default is `http://localhost:5080`.
- The product name and tagline are in [frontend/src/app/config.ts](../frontend/src/app/config.ts).

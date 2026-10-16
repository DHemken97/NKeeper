# Installation

- [Choosing how to run NKeeper](#choosing-how-to-run-nkeeper)
- [Setup scripts](#setup-scripts)
- [HTTPS and certificates](#https-and-certificates)
- [DNS for web sessions](#dns-for-web-sessions)
- [Upgrading](#upgrading)
- [Uninstalling](#uninstalling)
- [Docker Compose by hand](#docker-compose-by-hand)
- [Behind another reverse proxy](#behind-another-reverse-proxy)

## Choosing how to run NKeeper

| Mode | Command | Good for |
| --- | --- | --- |
| **Docker, Linux** (recommended) | `scripts/setup.sh` | Servers: everything in containers, easiest to upgrade and remove |
| **Docker Desktop, Windows** | `scripts\setup.cmd` | Windows machines that can run Docker Desktop |
| **Native, Linux** | `sudo scripts/setup.sh --mode native` | Servers where you'd rather use the distribution's nginx and PostgreSQL (Debian/Ubuntu, RHEL/Rocky/Alma/Fedora) |
| **IIS, Windows** | `scripts\setup.cmd -Mode Iis` (as administrator) | Windows servers that standardise on IIS. **Not yet tested on a real IIS server.** |

Every mode serves NKeeper over HTTPS (port 443 by default), with plain HTTP (port 80) redirecting to it.

**RDP and VNC need `guacd`**, Apache Guacamole's connection daemon. It has no Windows build, and Linux distributions don't package a current version, so it always runs as a container:
- **Docker modes:** as part of the Compose stack.
- **Native Linux:** in Docker or Podman, installing Podman if neither is present.
- **IIS:** in Docker Desktop, when it's available.

Without guacd, everything except RDP and VNC works, and the script tells you so.

**Requirements:** a 64-bit x86 or ARM machine (Linux, or Windows 10/11/Server). The first build downloads a few hundred MB of images and packages. Hosts you connect to must be reachable from the NKeeper server, not just from your PC.

## Setup scripts

Get the repository onto the machine (`git clone`, or download and extract it), then run the script for your OS from the repository folder. It:

1. Asks which names or addresses people will use to open NKeeper. The default is the machine's name and IP address.
2. Asks for the first administrator's user name, email and password. Leave the password empty to have one generated; it's shown once at the end.
3. Installs anything missing, builds NKeeper, starts it, and waits until it answers.
4. Removes the administrator's password from the configuration once the account exists.
5. Prints the address, the sign-in name and where the certificate authority file is.

Running the script again **upgrades** NKeeper and keeps your data, accounts and earlier answers. Pass options to change them.

### Options

Linux uses `--option value`; Windows uses `-Option value`. Run `scripts/setup.sh --help`, or `Get-Help scripts\setup.ps1 -Detailed`, for the full list.

| Linux | Windows | Purpose |
| --- | --- | --- |
| `--mode docker\|native` | `-Mode Docker\|Iis` | How to install (default: Docker) |
| `--hostname a,b` | `-HostName a,b` | Names and IP addresses clients use (default: this machine's name and IP) |
| `--local-only` | `-LocalOnly` | Only reachable from this machine (`https://localhost`) |
| `--https-port N` / `--http-port N` | `-HttpsPort N` / `-HttpPort N` | HTTPS port (default 443) and the HTTP port that redirects to it (default 80) |
| `--web-domain D` | `-WebDomain D` | Separate domain for web sessions (see [DNS](#dns-for-web-sessions)) |
| `--cert F --key F` | `-CertificateFile F -KeyFile F` (Docker), `-CertificateThumbprint T` (IIS) | Use your own certificate (see [HTTPS](#https-and-certificates)) |
| `--admin-user`, `--admin-email`, `--admin-password` | `-AdminUser`, `-AdminEmail`, `-AdminPassword` | First administrator, without asking |
| — | `-ConnectionString S` | IIS: use an existing PostgreSQL database instead of installing PostgreSQL |
| `--install-docker` | — | Install Docker without asking if it's missing |
| `--no-guacd` | `-NoGuacd` | Native modes: skip guacd (no RDP/VNC) |
| `-y`, `--yes` | `-Yes` | Ask nothing; use defaults and previous answers |
| `--uninstall [--purge]` | `-Uninstall [-Purge]` | Remove NKeeper (see [Uninstalling](#uninstalling)) |

Example of an unattended install:

```bash
scripts/setup.sh --yes --hostname nkeeper.corp.example.com,10.0.0.20 --admin-email ops@corp.example.com
```

### What each mode installs

**Docker (Linux and Windows):**
- **Prerequisites:** needs Docker with Compose v2. On Linux the script offers to install Docker with Docker's official script (`get.docker.com`). On Windows it offers Docker Desktop through winget; start Docker Desktop and run the script again once it says "Engine running".
- **Configuration:** writes `.env` in the repository folder (owner-only on Linux), with a random database password.
- **Stack:** builds and starts the stack: `postgres`, `backend`, `guacd` and `frontend`. Only `frontend` publishes ports.
- **Data:** kept in Docker volumes: `nkeeper_postgres-data` (database) and `nkeeper_tls` (certificates).
- **Firewall:** on Windows, the script adds a firewall rule for domain and private networks when run as administrator.

**Native Linux:**
- **Packages:** PostgreSQL, nginx, OpenSSL and Python 3 from the distribution. Impacket (for scanning Windows hosts) goes in its own Python environment in `/opt/impacket`; if it can't be downloaded, setup warns and only Windows scans are affected. The script creates the `nkeeper` database and login, and allows password sign-in for it over loopback.
- **Build tools:** a private .NET 10 SDK and Node.js 22 in `/var/cache/nkeeper-build`. Your checkout isn't touched; the build runs on a copy.
- **Application:** installed to `/opt/nkeeper` and run by the `nkeeper` systemd service as the `nkeeper` user, with sandboxing. It listens on `127.0.0.1:5080`, with settings and secrets in `/etc/nkeeper/nkeeper.env` (root only).
- **Web server:** nginx config in `/etc/nginx/conf.d/nkeeper.conf` and `nkeeper-tls.conf`, with snippets in `/etc/nginx/snippets/nkeeper/` and certificates in `/etc/nginx/nkeeper-tls/`.
- **guacd:** the `nkeeper-guacd` container (Docker) or service (Podman), on `127.0.0.1:4822`.
- **OS integration:** with SELinux enforcing, the script allows nginx to connect to the API and labels the files. It opens the ports in firewalld or ufw when either is active.

**IIS (Windows, as administrator):**
- **Prerequisites:**
  - **IIS:** enabled, with WebSockets and HTTP Redirection.
  - **Installed with winget where missing:** the .NET 10 SDK, the ASP.NET Core Hosting Bundle, Node.js LTS and PostgreSQL 17.
  - **PostgreSQL superuser password:** generated, and kept in `C:\ProgramData\NKeeper\state.json`, which only administrators can read.
  - **Existing PostgreSQL:** the script asks for its `postgres` password; or pass `-ConnectionString`.
- **Site:** the build is published to `C:\inetpub\nkeeper` as the **NKeeper** site, in its own app pool. The app pool never idles or recycles, because open sessions live in it. The API serves the web frontend itself.
- **Secrets:** the database connection is kept in the site's `web.config`, which only administrators and the app pool can read.
- **HTTPS:** certificates are issued into `LocalMachine\My`. A small **NKeeper HTTP redirect** site sends HTTP to HTTPS. If the stock *Default Web Site* holds port 80, the script offers to stop it.
- **guacd and firewall:** runs `nkeeper-guacd` in Docker Desktop when available, and opens Windows Firewall for domain and private networks.

## HTTPS and certificates

NKeeper needs HTTPS anywhere but `localhost`: its session cookie is `Secure`, so over plain HTTP from another machine, sign-in appears to fail.

**Default: NKeeper's private CA.** On first start NKeeper creates its own certificate authority, and a server certificate covering:
- every name and IP you gave;
- `localhost`;
- `*.web.<name>` for each name (web sessions).

The certificate is renewed automatically when the names change. In Docker, it's also renewed within 30 days of expiry on any restart. The setup script saves the CA's certificate as **`nkeeper-ca.crt`** in the repository folder. Install it as a trusted root on each client machine:

| Client | How |
| --- | --- |
| Windows | `certutil -addstore -f Root nkeeper-ca.crt` (administrator), or Group Policy for many machines |
| macOS | Open it in Keychain Access, add to **System**, and set it to *Always Trust* |
| Linux | Copy it to `/usr/local/share/ca-certificates/nkeeper-ca.crt` and run `sudo update-ca-certificates` (Firefox may need it imported in its own settings) |

Without it, browsers warn. Web sessions inside NKeeper won't load at all, because a framed page can't show a certificate warning.

**Your own certificate.** Use a certificate from your company's CA or a public CA. It must cover the names people use and, for web sessions, `*.web.<name>` or `*.<web domain>`.
- **Linux:** `scripts/setup.sh --cert fullchain.pem --key privkey.pem`
- **Windows with Docker:** `-CertificateFile fullchain.pem -KeyFile privkey.pem`
- **IIS:** import it into `LocalMachine\My` and pass `-CertificateThumbprint <thumbprint>`.

In Docker, your certificate is stored as `custom.crt`/`custom.key` in the `nkeeper_tls` volume and always wins over the private CA. To go back, delete those two files from the volume:

```bash
docker compose exec frontend rm /etc/nginx/tls/custom.crt /etc/nginx/tls/custom.key
docker compose restart frontend
```

## DNS for web sessions

Web sessions (HTTP/HTTPS through NKeeper) each run on their own address, `nkw-<id>.web.<name>`, so a proxied web app can never touch NKeeper itself. Other machines must be able to resolve those names, so add a **wildcard DNS record** pointing at the NKeeper server:

```
*.web.nkeeper.corp.example.com.   A   10.0.0.20
```

Or use a separate domain (recommended in production, so proxied apps can't set cookies for NKeeper's domain). Pass `--web-domain nkeeper-web.corp.example.net` and create `*.nkeeper-web.corp.example.net`. Everything else works without the record. NKeeper must be opened **by name** for web sessions, not by IP address.

For a quick trial without DNS changes, wildcard DNS services such as `sslip.io` map names like `10-0-0-20.sslip.io` (and every subdomain) to that IP. Pass that name as `--hostname`. This depends on an outside DNS service, so don't use it in production.

## Upgrading

```bash
git pull
scripts/setup.sh          # or scripts\setup.cmd on Windows; add --yes to skip questions
```

The script rebuilds and restarts NKeeper. Database migrations run automatically on start. Open sessions end during the restart. Take a [backup](administration.md#backups-and-restores) first.

## Uninstalling

```bash
scripts/setup.sh --uninstall           # keeps the database (and .env / certificates)
scripts/setup.sh --uninstall --purge   # also deletes the database and certificates
```

On Windows: `scripts\setup.cmd -Uninstall [-Purge]`. In IIS mode, PostgreSQL itself stays installed; `-Purge` drops the `nkeeper` database.

## Docker Compose by hand

The setup script does this for you; these are the manual steps.

```bash
cp .env.example .env
# Set POSTGRES_PASSWORD (letters and digits, e.g. `openssl rand -hex 24`), NKEEPER_SERVER_NAMES,
# and for the first start the NKEEPER_BOOTSTRAP_ADMIN_* values (see Administration).
docker compose up -d --build
```

| Service | Role | Ports |
| --- | --- | --- |
| `postgres` | Database, volume `nkeeper_postgres-data` | none |
| `backend` | The API. It applies migrations on start (`NKEEPER_MIGRATE_ON_STARTUP`) | none |
| `guacd` | RDP/VNC gateway, used only by the API | none |
| `frontend` | nginx: serves the app, proxies `/api` to the backend, terminates HTTPS | `127.0.0.1:8080` HTTP; `NKEEPER_HTTPS_BIND:NKEEPER_HTTPS_PORT` HTTPS (default `127.0.0.1:8443`); `NKEEPER_HTTP_REDIRECT_PORT` (default 8081) redirects to HTTPS |

With the defaults, NKeeper is at <http://localhost:8080> and <https://localhost:8443>. To serve other machines:
- set `NKEEPER_HTTPS_BIND=0.0.0.0`, `NKEEPER_HTTPS_PORT=443`, `NKEEPER_HTTP_REDIRECT_PORT=80`;
- set `NKEEPER_SERVER_NAMES` to your names;
- run `docker compose up -d`.

Export the CA with `docker compose cp frontend:/etc/nginx/tls/ca.crt nkeeper-ca.crt`. All variables are described in [Configuration](configuration.md#docker-compose-variables).

```bash
docker compose logs -f backend      # API logs
docker compose down                 # stop (data is kept)
docker compose down -v              # stop and DELETE the database and certificates
```

## Behind another reverse proxy

To put NKeeper behind an existing proxy or load balancer (Caddy, Traefik, F5…) that terminates TLS:

1. Point the proxy at the frontend container's HTTP port (`NKEEPER_HTTP_BIND`/`NKEEPER_HTTP_PORT`), and keep that port private to the proxy.
2. Forward the original `Host` header, and send `X-Forwarded-Proto: https` and `X-Forwarded-For`. The bundled nginx passes an existing `X-Forwarded-Proto` through.
3. Allow WebSocket upgrades and long idle times (an hour) for `/api/ssh/`, `/api/rdp/`, `/api/vnc/` and `/api/spice/`.
   Allow request bodies up to 40 MB for `/api/hosts/import` (mRemoteNG imports send the whole file). Many proxies limit uploads to about 1 MB by default.
4. Route the web-session wildcard names (`*.web.<name>`) to NKeeper too, with a certificate that covers them.
5. Set `ForwardedHeaders__ForwardLimit=2` on the backend, so rate limiting sees real client IPs through both proxies. Do it in a `docker-compose.override.yml` (git-ignored), which Compose reads automatically:

   ```yaml
   services:
     backend:
       environment:
         ForwardedHeaders__ForwardLimit: "2"
   ```

6. Keep `NKEEPER_REQUIRE_SECURE_COOKIES=true`.

# Troubleshooting

Start with the logs: `docker compose logs -f backend` (Docker), `journalctl -u nkeeper -f` (native Linux), or the Windows Application event log (IIS). See [Administration](administration.md#logs).

## Installing and starting

| Symptom | Cause / fix |
| --- | --- |
| Setup: "Port 443 is already in use" | Another web server holds the port. Stop it, or use `--https-port`/`--http-port` (`-HttpsPort`/`-HttpPort`). |
| Setup (Windows): "Docker Desktop is using Windows containers" | Switch Docker Desktop to Linux containers (tray icon menu) and run the script again. |
| Setup (Linux): "Docker isn't running or you can't use it" | `sudo systemctl start docker`, or run the script with `sudo`, or add yourself to the `docker` group (then sign in again). |
| Setup: "guacd didn't start", or RDP/VNC say "remote desktop gateway (guacd) isn't reachable" | Check the guacd container: `docker logs nkeeper-guacd` or `journalctl -u nkeeper-guacd` (native), `docker compose logs guacd` (Docker). In native IIS mode, Docker Desktop must be running. |
| `required variable POSTGRES_PASSWORD is missing a value` | `.env` is missing or has no `POSTGRES_PASSWORD`. Run the setup script, or create `.env` from `.env.example`. |
| "A database already exists but .env has no POSTGRES_PASSWORD" | `.env` was lost. Restore it from a backup; or, to start over and **delete all data**, `scripts/setup.sh --uninstall --purge`. |
| `password authentication failed` after changing `POSTGRES_PASSWORD` | PostgreSQL only reads it when its volume is first created. Change it inside PostgreSQL (`ALTER ROLE`), or put the old value back. |
| API exits: `ConnectionStrings:NKeeper must be configured` | Set the connection string (user secrets in development, `.env` or the settings file when installed). |
| API exits: `Failed to create the bootstrap administrator: Passwords must…` | The password needs 12+ characters with upper case, lower case and a digit. |
| IIS: the site answers `500.19` or `500.30` | `500.19`: the ASP.NET Core Module isn't installed. Repair the Hosting Bundle, then run setup again. `500.30`: the app failed to start. Check the event log, or set `stdoutLogEnabled="true"` in `web.config`. |

## Signing in

| Symptom | Cause / fix |
| --- | --- |
| Sign-in "succeeds" but you're sent back to the sign-in page | The browser dropped the `Secure` session cookie because the page isn't HTTPS (and isn't `localhost`). Use the `https://` address. |
| Browser warns about the certificate | Install `nkeeper-ca.crt` as a trusted root on that computer, or use your own certificate. See [Installation](installation.md#https-and-certificates). |
| `429 Too many attempts` | The sign-in rate limit. Wait for the `Retry-After` time. |
| The right password is rejected after several failures | The account is locked for 15 minutes after 5 failures. |
| "Can't reach the server" on the sign-in page | The API isn't running, or (in development) the Vite proxy target is wrong (`DEV_API_PROXY_TARGET`). |
| A custom client gets `400` with `"code": "csrf_token_invalid"` | Fetch `GET /api/auth/antiforgery` (keeping cookies) and send the token as `X-XSRF-TOKEN`. Fetch a new one after signing in or out. |

## Connecting

| Symptom | Cause / fix |
| --- | --- |
| "The name could not be resolved" / "host is unreachable" | The **server** makes the connection. The host must be resolvable and reachable from it (in Docker, from the backend container), not just from your PC. |
| "Server identity changed" (a host key or certificate has changed) | The server presented a different identity from the trusted one. Check with its owner. If the change is expected, use **Forget trusted keys…** in the host details and connect again. |
| SSH "Authentication failed" with the right password | The server may accept only keys, or the account may be locked. Try the same login with `ssh` from the NKeeper server. |
| RDP fails with a security or NLA error | Set the RDP service's **Security** to *NLA only* or *TLS only* to match the server, and check that the credential's domain is correct. |
| A session tab says "Couldn't reach the NKeeper server" | The WebSocket was blocked. Behind your own reverse proxy, allow WebSocket upgrades and forward the original `Host` for `/api/ssh/`, `/api/rdp/`, `/api/vnc/` and `/api/spice/` (see [Installation](installation.md#behind-another-reverse-proxy)). |
| A web session shows "needs NKeeper to be opened by a host name" | Web sessions need a name, not an IP address. Open NKeeper by name. |
| A web session stays blank from other computers | They can't resolve `nkw-…` names, or don't trust the certificate. Add the wildcard DNS record and install the CA. See [Installation](installation.md#dns-for-web-sessions). |
| KVM: "libvirt refused the request" | The SSH user needs access to `qemu:///system`, usually through membership in the `libvirt` group. |
| KVM: the console doesn't open | The VM must be running with a SPICE display that isn't TLS-only, or a VNC display, listening on a TCP port. The hypervisor's sshd must allow TCP forwarding. |
| KVM: a VNC console says libvirt didn't give NKeeper the password | libvirt reveals console passwords only to accounts with full (read-write) access. Give the SSH user that access (e.g. the `libvirt` group), or remove the VM's VNC password. |

## Development

| Symptom | Cause / fix |
| --- | --- |
| Database tests show as Skipped | Neither Docker nor `NKEEPER_TEST_POSTGRES` is available. See [Development](development.md#tests). |
| `dotnet ef` not found | Run `dotnet tool restore` in `backend/`. |

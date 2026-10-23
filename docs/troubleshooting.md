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
| **Sign in with …** ends with "Single sign-on didn't complete" | The provider refused or the answer didn't check out. The API log has the reason. Common causes: the redirect URI registered at the provider isn't exactly `https://<address>/api/auth/sso/callback` (including the port), the client secret expired, `SingleSignOn__Authority` uses `common` instead of the tenant id, or the page isn't HTTPS (the provider's correlation cookie needs it). |
| "Your account isn't allowed to sign in to NKeeper" after single sign-on | The user isn't in `SingleSignOn__RequiredGroups`, the account is disabled, or their username or email already belongs to another NKeeper account. The audit log's failed sign-in shows which. |
| Single sign-on works, but nobody becomes an administrator | The groups claim isn't in the token, or holds different values. With Entra ID, add a groups claim to the app registration and use the groups' **object ids**. Users in more than 200 groups get no groups claim: use *Groups assigned to the application*, or app roles with `SingleSignOn__GroupsClaim: roles`. |
| "Your account needs a sign-in code sent by email, and it couldn't be sent" | The SMTP server refused or couldn't be reached. Use **Settings → Email → Send test email** to see the error. In Docker, the backend container must be able to reach the SMTP server's port. |
| The sign-in code email never arrives | Check the spam folder and the sender address (some servers only accept their own domain as sender). **Send a new code** after 30 seconds. An administrator can turn **Email 2FA challenge code** off for the user under **Users**. |
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
| Migration: "can't reach … over SSH" | Proxmox migrations, and live libvirt ones, run SSH as root from one host to the other without a password. Check `ssh -o BatchMode=yes root@<other host> true` on the old host (and the new one, for rolling back a live move): it needs root's SSH key in the other host's `authorized_keys`, and its host key known. Without it, libvirt VMs can still move offline. |
| Migration: "mark … as shared" | A Proxmox directory storage on a file system NKeeper moves between nodes (e.g. on DRBD) must be marked **shared** (Datacenter ▸ Storage), or Proxmox would copy its disks. |
| Migration: "… is an overlay of …" | The disk is a qcow2 overlay (a snapshot or linked clone). NKeeper doesn't copy backing chains: merge it into one image first (`qemu-img convert -O qcow2 overlay.qcow2 merged.qcow2`), or copy the chain by hand. |
| Migration: a local disk copy is slow | NKeeper is relaying it, because the old host can't reach the new one over SSH as root. Give the old host's root SSH access to the new one (its key in the new host's `authorized_keys`) and it copies directly, or live. |
| Migration: "Unmount … failed: target is busy" (and it was put back) | Something else on the old host uses that file system: another VM's disk, a shell in that folder, a backup. Stop it and migrate again. |
| Migration: "… is Inconsistent, not UpToDate" | The DRBD resource on the new host is still syncing. Wait until `drbdadm status` shows UpToDate there. |
| Migration: a live move turned offline | The check says why: the disks are image files on a file system that can't be mounted on two hosts at once, the old host can't reach the new one over SSH, or it's a container. |
| Storage: "needs root" | `iscsiadm`, `multipath` and `drbdsetup` only run as root. Use a root credential, or let the user run `sudo` without a password. |
| Storage: a section is missing | NKeeper shows only the tools it finds on the host (looking in `/usr/sbin` and `/sbin` too). Install open-iscsi / iscsi-initiator-utils, multipath-tools / device-mapper-multipath, or drbd-utils. |
| Storage: "iSCSI discovery … failed: cannot make connection" | The **host**, not the NKeeper server, connects to the portal. Check the address and port (3260 by default) from the host. |
| Storage: Flush, Secondary or Down is refused | Something still uses the device (a mount, LVM, a VM or another map). Stop using it first; NKeeper shows the tool's own message. |
| KVM: "libvirt refused the request" | The SSH user needs access to `qemu:///system`, usually through membership in the `libvirt` group. |
| KVM: the console doesn't open | The VM must be running with a SPICE display that isn't TLS-only, or a VNC display, listening on a TCP port. The hypervisor's sshd must allow TCP forwarding. |
| Proxmox console: "Proxmox refused the login" | Check the username, password and realm (`root@pam`, `admin@pve`). Accounts with two-factor sign-in can't be used: use an API token (`user@realm!tokenid`, with its secret as the password). |
| Proxmox console or task: "isn't allowed to do this" | The account needs **VM.Console** on the VM for consoles, **VM.PowerMgmt** for power actions and **VM.Migrate** for migrations (in Proxmox: Datacenter ▸ Permissions, e.g. the PVEVMUser or PVEVMAdmin role on `/vms/<id>`). |
| Proxmox: no "Proxmox console" under Connect, or no Tasks ▸ Proxmox | The VM must be linked to its node by a scan of the node (which records its Proxmox VM ID), and the node needs a Proxmox service that you can see. |
| Proxmox: a shutdown never finishes | The guest didn't respond to the shutdown request. Install the QEMU guest agent or make sure it handles ACPI, or use Stop. |
| KVM on Proxmox: "Proxmox refused the request" | `pvesh` and `qm` only run as root. Use a root credential, or let the user run `sudo` without a password. |
| KVM on Proxmox: the console of a VM on another node doesn't open | NKeeper reaches other nodes the way Proxmox does, with `ssh root@<node>` from the node you connected to. Check that `ssh root@<node> true` works there without a prompt (`pvecm updatecerts` repairs the cluster's keys), or add that node to NKeeper and open the console from it. The API's log says why the console proxy stopped. |
| KVM: a VNC console says libvirt didn't give NKeeper the password | libvirt reveals console passwords only to accounts with full (read-write) access. Give the SSH user that access (e.g. the `libvirt` group), or remove the VM's VNC password. |

## Development

| Symptom | Cause / fix |
| --- | --- |
| Database tests show as Skipped | Neither Docker nor `NKEEPER_TEST_POSTGRES` is available. See [Development](development.md#tests). |
| `dotnet ef` not found | Run `dotnet tool restore` in `backend/`. |

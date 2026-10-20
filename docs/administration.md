# Administration

- [The first administrator](#the-first-administrator)
- [Users and access](#users-and-access)
- [Active Directory sign-in](#active-directory-sign-in)
- [Moving to a new server](#moving-to-a-new-server)
- [Backups and restores](#backups-and-restores)
- [Audit log](#audit-log)
- [Logs](#logs)
- [Database migrations](#database-migrations)

## The first administrator

There's no public sign-up. **The setup scripts create the first administrator for you**, then remove its password from the configuration. This section is for manual installs.

The first administrator is created by a guarded step at API start-up:

- **Disabled by default.** Nothing happens unless `BootstrapAdmin:Enabled=true`.
- **Development only by default.** In any other environment it's skipped with a warning unless `BootstrapAdmin:AllowOutsideDevelopment=true` is also set.
- **Create-only.** If a user with that username or email exists, it's left untouched. The bootstrap never resets a password, so leaving it enabled is harmless.
- The account gets the **Administrator** role. The password must have at least 12 characters, with upper case, lower case and a digit. Start-up fails with a clear message if it doesn't.

With Docker Compose, set these in `.env`, run `docker compose up -d`, and sign in:

```
NKEEPER_BOOTSTRAP_ADMIN_ENABLED=true
NKEEPER_BOOTSTRAP_ADMIN_ALLOW_OUTSIDE_DEVELOPMENT=true
NKEEPER_BOOTSTRAP_ADMIN_USERNAME=admin
NKEEPER_BOOTSTRAP_ADMIN_EMAIL=admin@example.com
NKEEPER_BOOTSTRAP_ADMIN_PASSWORD=<strong password>
```

Then set `NKEEPER_BOOTSTRAP_ADMIN_ENABLED=false`, delete the password, and run `docker compose up -d` again.

## Users and access

Administrators manage accounts under **Users** in the top bar.

- **Roles.**
  - **Administrators** see and manage every host, add hosts, and manage users.
  - **Users** see only what they've been granted.
- **Access grants** give a user either:
  - a **group** of hosts: a tag such as `site: Datacenter A`, or just `site` for any value;
  - or **individual hosts**.

  Each grant has a level: **Use** (see and connect) or **Manage** (also edit, delete and forget trusted keys). Group grants follow the tags, so newly tagged hosts are included automatically. The editor shows how many hosts each grant covers.
- **New users:** the administrator gives them a password (there's a generator), and by default they must choose their own at first sign-in. Users sign in with their username or email, so the email is required and unique.
- **Changes apply immediately.** Accounts are re-checked on every request. Disabling, deleting, promoting or demoting a user, or resetting their password, signs them out everywhere and ends their open SSH, RDP, VNC, web and console sessions. Grant changes apply to the user's next action; open sessions continue.
- **No lock-outs.** Administrators can't demote, disable or delete themselves, and NKeeper always keeps at least one active administrator.

The API enforces all of this:
- hosts a user can't see answer `404`, so their existence isn't revealed;
- hosts they can see but not manage answer `403` to changes;
- every kind of session looks up hosts through the same rules.

Account changes are logged with who made them.

**Limits per user:** 20 open SSH/RDP/VNC sessions, 20 web sessions and 10 VM consoles. The interface shows one console at a time.

## Email and sign-in codes

**Settings → Email** (administrators only) is where NKeeper's SMTP server is set: server, port, encryption (STARTTLS, TLS from the start, or none for a local relay), optional username and password, and the sender address. **Send test email** sends a message to your own address with the values in the form, before you save them. The password is stored encrypted like host credentials and is never shown again: leave the field empty to keep it.

With email set up, **Email 2FA challenge code** can be turned on for any user, when creating them or under **Users**. Their sign-in then has a second step:

1. They sign in as usual: password, Active Directory or single sign-on.
2. NKeeper emails a random 9-digit code to the account's address. Until it's entered, they have no access at all.
3. They type the code on the sign-in page. It expires after 10 minutes. **Send a new code** replaces it (at most every 30 seconds). Five wrong codes cancel the sign-in.

- **Email must stay working.** The option can't be turned on while email is off, and email can't be turned off while any user has it. If the server can't send the code, the sign-in fails with a message to contact an administrator. Keep one administrator without the option as a way in.
- **Audit log.** Sign-ins completed with a code are recorded as *emailed code*. Wrong codes are recorded as failed sign-ins.
- **Moving to a new server** carries the email settings (password included, inside the encrypted file) and each user's option.

## Moving to a new server

Move a whole instance (Docker, native Linux or IIS, to any of them) from the UI. Use this, not a database dump: a dump also carries the old server's encryption keys, and needs the same database tools on both sides.

**What moves:**
- user accounts, with their roles, passwords (as hashes), lockouts, access grants and saved workspace grouping and sort. [Active Directory](#active-directory-sign-in) accounts stay domain accounts: configure `ActiveDirectory__*` on the new server the same way before importing;
- hosts, with their tags, services, credentials, hardware details, VM links, jump hosts and trusted keys.

**What doesn't move:**
- sign-in sessions: everyone signs in again on the new server;
- open sessions;
- the audit log and session recordings: keep the old server's database if you need them;
- the old server's encryption keys: the new server re-encrypts every stored password with its own;
- configuration (`.env`, `/etc/nkeeper/nkeeper.env` or `state.json`, including `WebProxy:BaseDomain`), TLS certificates and logs. Set those up on the new server as for any install.

**Steps:**
1. **Install the new server** with the setup script for its platform, and sign in there with the administrator the installer created. Don't add hosts or users: only an empty instance accepts an import.
2. **Export on the old server:** account menu ▸ **Move to a new server…**.
   - Enter your password, and keep the generated passphrase (or choose one of 16+ characters). Store it in a password manager, or write it down, never next to the file.
   - Leave **Lock this server after the export** on, so nobody makes changes there that the new server won't get. Hosts and users then can't be changed; connecting still works.
   - **Export and download** saves a `.nkeeper` file.
3. **Copy the file** to a computer that can reach the new server, over a channel you trust (not email or chat).
4. **Import on the new server:** account menu ▸ **Move to a new server…** ▸ choose the file and enter the passphrase.
   - **Check the file** decrypts it and shows what it holds, without changing anything.
   - Tick the confirmation, then **Import**. Everything is imported in one transaction, or nothing is.
5. **Sign in again** with your account from the old server: the installer's administrator was replaced by the imported accounts. Open a session or two to check that stored passwords work.
6. **Clean up:**
   - delete the `.nkeeper` file everywhere it was copied;
   - point DNS and users at the new server;
   - retire the old one (stop it, then back up and destroy its database). If the move is called off, **Unlock** the old server on the same page.

**Requirements and limits:**
- The new server must run the same NKeeper version as the old one, or a newer one. A file from a newer NKeeper is refused, with a message to update first.
- Files can be up to 256 MB.
- Exports and imports are limited to 10 per administrator every 10 minutes.

## Active Directory sign-in

With `ActiveDirectory__Enabled`, people can sign in to NKeeper with their domain account as `jdoe`, `CORP\jdoe` or `jdoe@corp.example.com`. NKeeper checks the password with an LDAP bind to a domain controller, over LDAPS or StartTLS. At their first sign-in it creates an NKeeper account for them, marked **AD** under **Users**. The [settings](configuration.md#api-settings) are the `ActiveDirectory__*` rows.

```yaml
# docker-compose.override.yml
services:
  backend:
    environment:
      ActiveDirectory__Enabled: "true"
      ActiveDirectory__Domain: CORP
      ActiveDirectory__DnsDomain: corp.example.com
      ActiveDirectory__RequiredGroup: CN=NKeeper Users,OU=Groups,DC=corp,DC=example,DC=com
      ActiveDirectory__AdministratorsGroup: CN=NKeeper Admins,OU=Groups,DC=corp,DC=example,DC=com
```

- **Who can sign in.** Without `RequiredGroup`, any enabled account in the domain can sign in. It sees no hosts until you grant some.
- **Roles.** With `AdministratorsGroup`, membership is checked at every sign-in, and the Administrator role is granted or removed to match. Without it, you assign roles under **Users** as usual.
- **Passwords stay in Active Directory.** Domain accounts can't change their password in NKeeper, and administrators can't reset it there. Disabling an account in NKeeper still blocks it. NKeeper locks an account for 15 minutes after 5 failed sign-ins, before Active Directory's own lockout threshold is reached.
- **Local accounts keep working.** A local account keeps its own password. A domain account with the same name can't take it over, so keep the bootstrap administrator local as a way in if the directory is down.
- **Certificates.** The domain controller's certificate must be trusted by the NKeeper server. In Docker, mount your CA certificate into the backend container and point OpenLDAP at it with the `LDAPTLS_CACERT` environment variable, e.g. `LDAPTLS_CACERT: /certs/corp-ca.pem`.
- **RDP with the user's own login.** With `PassCredentialsToRemoteSessions` (the default), RDP services offer **My sign-in** (`CORP\jdoe`). The domain password is kept encrypted with the user's server-side session and deleted when they sign out or the session expires or is revoked. Set it to `false` to keep no passwords; users then type their login or use host credentials.

## Single sign-on

With `SingleSignOn__Enabled`, the sign-in page gets a **Sign in with …** button for an OpenID Connect identity provider: Microsoft Entra ID (formerly Azure AD), Okta, Google, Keycloak, Amazon Cognito and others. At a user's first sign-in, NKeeper creates an account for them, marked **SSO** under **Users**. The [settings](configuration.md#api-settings) are the `SingleSignOn__*` rows.

> **Entra ID or "AWS AD"?** For Microsoft 365 / Entra ID, use this section. AWS Managed Microsoft AD (AWS Directory Service) is a regular Active Directory domain: use [Active Directory sign-in](#active-directory-sign-in) against its domain controllers instead. AWS IAM Identity Center only offers SAML to custom applications, which NKeeper doesn't support; sign in through Entra ID, Okta or Cognito in front of it instead.

**Setting it up with Microsoft Entra ID:**

1. In the Entra admin center, go to **App registrations → New registration**. Choose *Accounts in this organizational directory only*, and add a **Web** redirect URI: `https://<your NKeeper address>/api/auth/sso/callback`. Include the port if it isn't 443. It must match exactly.
2. Under **Certificates & secrets**, create a client secret. Note its value, the **Application (client) ID** and the **Directory (tenant) ID**.
3. Optional: under **Token configuration → Add groups claim**, choose *Security groups* (or *Groups assigned to the application*, which avoids the 200-group limit), to use `RequiredGroups` and `AdministratorGroups` with group object ids. Alternatively, define **App roles** and set `GroupsClaim` to `roles`.
4. Configure NKeeper:

```yaml
# docker-compose.override.yml
services:
  backend:
    environment:
      SingleSignOn__Enabled: "true"
      SingleSignOn__DisplayName: Microsoft
      SingleSignOn__Authority: https://login.microsoftonline.com/<tenant-id>/v2.0
      SingleSignOn__ClientId: <application-id>
      SingleSignOn__ClientSecret: <secret value>
      SingleSignOn__SubjectClaim: oid
      SingleSignOn__RequiredGroups__0: <object id of "NKeeper Users">
      SingleSignOn__AdministratorGroups__0: <object id of "NKeeper Admins">
```

Use the tenant-specific authority, not `common` or `organizations`: NKeeper checks that the token comes from exactly that issuer.

- **Accounts are tied to the provider's id for the person** (`SubjectClaim`), never to a name or email. A username or email that already belongs to an NKeeper account (local, Active Directory, or another provider id) is refused, never linked, so nobody can take over an existing account by renaming themselves at the provider.
- **Who can sign in.** Without `RequiredGroups`, anyone the provider signs in gets an account. It sees no hosts until you grant some. Refused sign-ins are in the audit log, with the reason.
- **Roles.** With `AdministratorGroups`, membership is checked at every sign-in, and the Administrator role is granted or removed to match. Without it, you assign roles under **Users** as usual.
- **No NKeeper password.** Single sign-on accounts can't sign in with a password, change one or have one reset. Disabling an account in NKeeper still blocks it. Signing out of NKeeper doesn't sign the user out of the provider.
- **Local accounts keep working.** Keep the bootstrap administrator local as a way in if the provider is unreachable.
- **Behind your own reverse proxy**, pass the original `Host` (with its port) and `X-Forwarded-Proto` to NKeeper, as the bundled nginx does, so the redirect URI it sends matches the registered one.

## Backups and restores

> Backups aren't automated yet. Schedule one of the commands below (cron, systemd timer or Task Scheduler), keep copies off the server, and test a restore.

Everything NKeeper stores is in its PostgreSQL database: hosts, credentials, accounts, trusted keys, and the **Data Protection keys** that encrypt the stored passwords.

- **Native Linux:** those keys are themselves encrypted with `/etc/nkeeper/data-protection.pem`. A database backup can't decrypt anything without that file, and the file is useless without the database. **Back up the file once, and keep it apart from the database backups** (e.g. in a password manager or a separate vault). If it's lost, the stored credentials can't be recovered: NKeeper won't start until it's restored. Backups taken before the file existed (installs upgraded from earlier versions) still hold the keys unencrypted: destroy them, or protect them like the passwords themselves.
- **Docker and IIS:** the keys are stored unencrypted, so a database backup is complete. Keep it as safe as the passwords themselves. Without the database, the stored secrets can't be recovered; with it, anyone can decrypt them.

Also keep:
- **Docker:** `.env`, for its database password and settings;
- **Native Linux:** `/etc/nkeeper/nkeeper.env`, and `data-protection.pem` as above;
- **IIS:** `C:\ProgramData\NKeeper\state.json`.

Optionally keep the certificates too: the `nkeeper_tls` volume, `/etc/nginx/nkeeper-tls`, or the IIS certificates. If they're lost, NKeeper issues a new private CA, and clients must trust the new one.

### Docker

Back up:

```bash
docker compose exec -T postgres sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc -f /tmp/nkeeper.dump'
docker compose cp postgres:/tmp/nkeeper.dump ./nkeeper-$(date +%F).dump
docker compose exec -T postgres rm /tmp/nkeeper.dump
```

(On Windows, these commands work in PowerShell too. Name the file without `$(date …)`, e.g. `nkeeper.dump`.)

Restore, replacing the current data:

```bash
docker compose stop backend
docker compose cp ./nkeeper-2026-10-16.dump postgres:/tmp/restore.dump
docker compose exec -T postgres sh -c 'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists --no-owner /tmp/restore.dump && rm /tmp/restore.dump'
docker compose start backend
```

To check a backup without touching the live data, restore it into a scratch database instead:
- create it with `createdb -U "$POSTGRES_USER" nkeeper_check`;
- run `pg_restore … -d nkeeper_check`;
- look at the data;
- drop it with `dropdb`.

### Native Linux

These use the same PostgreSQL tools as the Docker commands; adjust paths to taste.

```bash
sudo -u postgres pg_dump -d nkeeper -Fc -f /var/backups/nkeeper-$(date +%F).dump
# Once, kept somewhere else: the certificate that decrypts the keys in those dumps.
sudo cat /etc/nkeeper/data-protection.pem
# Restore (to a new machine: put data-protection.pem back first, owner root:nkeeper, mode 640):
sudo systemctl stop nkeeper
sudo -u postgres pg_restore -d nkeeper --clean --if-exists --no-owner --role=nkeeper /var/backups/nkeeper-2026-10-16.dump
sudo systemctl start nkeeper
```

### IIS

Use `pg_dump.exe` and `pg_restore.exe` from `C:\Program Files\PostgreSQL\17\bin`, with the same options, connecting as `postgres`. Stop the **NKeeper** app pool before restoring.

## Audit log

**Reports ▸ Audit log** (administrators) is NKeeper's record of who did what. It's kept in the database, separately from the application logs.

**What's recorded:**
- **Sign-ins:** successful, failed (with the name that was tried, never the password), and sign-outs.
- **Sessions:** every SSH, Telnet, RDP, VNC, RPC, web, iDRAC console and VM console session.
- **Files:** every SFTP upload, download, rename, delete and new folder, with its path and size.
  - Each session's start says what it connected to, e.g. *SSH · root@10.0.0.5:22*. Its end gives the duration and, for web sessions and consoles, why it ended.
  - A session that couldn't connect is recorded with the reason, e.g. a refused connection or a rejected login.
- **Changes:** hosts added, changed, deleted, imported, bulk-edited and scanned; trusted keys forgotten; users added, changed, deleted, their passwords reset and their access changed; iDRAC power actions; moves to a new server.

Each entry has the time, the user, the host (named as it was then, so the trail still reads correctly after a rename or deletion) and the client's address. Search by text, filter by what happened and by date, and page back with **Load older**. **Download CSV** exports what's shown, up to 100,000 rows.

**Recordings:** SSH and Telnet sessions are recorded as they appeared on screen, and **▶ Play recording** replays one at its original pace, or 2×, 4× or 8×. Pauses longer than two seconds are shortened.
- **Only output is recorded**, never keystrokes. Passwords typed at a prompt (which the terminal doesn't echo) aren't in it, but anything the screen showed is, e.g. a file `cat` printed. Treat recordings as sensitive. Only administrators can play them.
- **Size limit:** a recording stops at 50 MB and is marked as cut short; the session itself goes on.
- **Not recorded yet:** RDP and VNC sessions.
- **Turning it off:** set `Auditing__RecordTerminalSessions=false`.

**Retention:** events are kept for 365 days and recordings for 90, then deleted daily. Both can be changed under `Auditing__*` in [Configuration](configuration.md#api-settings). Audit events aren't carried over by a move to a new server.

## Logs

| Install | API log | Web server |
| --- | --- | --- |
| Docker | `docker compose logs -f backend` | `docker compose logs -f frontend` |
| Native Linux | `journalctl -u nkeeper -f` | `/var/log/nginx/` |
| IIS | Windows **Application** event log (source *IIS AspNetCore Module V2*); for start-up errors set `stdoutLogEnabled="true"` in `C:\inetpub\nkeeper\web.config` | IIS logs in `C:\inetpub\logs` |

**What's logged:**
- **Sign-ins:** failed sign-ins, by user ID only.
- **Accounts:** account changes, with who made them.
- **Sessions:** every session start and end, with the user, host, credential ID and duration.

Secrets are never logged. These are application logs; for who did what, use the [audit log](#audit-log).

## Database migrations

The schema changes through Entity Framework Core migrations, committed in the repository. All installs apply pending migrations when the API starts. That's `Database:MigrateOnStartup=true`, which the setup scripts and Compose set.

For controlled rollouts:
1. Turn that off (`NKEEPER_MIGRATE_ON_STARTUP=false` in Docker).
2. Apply an idempotent SQL script, generated from the repository as described in [Development](development.md#database-migrations), as a deployment step.

Back up before upgrading. Migrations are designed to be safe, but there's no automatic downgrade.

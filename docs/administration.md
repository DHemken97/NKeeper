# Administration

- [The first administrator](#the-first-administrator)
- [Users and access](#users-and-access)
- [Backups and restores](#backups-and-restores)
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

## Backups and restores

> Backups aren't automated yet. Schedule one of the commands below (cron, systemd timer or Task Scheduler), keep copies off the server, and test a restore.

Everything NKeeper stores is in its PostgreSQL database: hosts, credentials, accounts, trusted keys, and the **Data Protection keys** that encrypt the stored passwords. A database backup is therefore complete. Keep it as safe as the passwords themselves. Without the database, the stored secrets can't be recovered; with it, anyone can decrypt them.

Also keep:
- **Docker:** `.env`, for its database password and settings;
- **Native Linux:** `/etc/nkeeper/nkeeper.env`;
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
# Restore:
sudo systemctl stop nkeeper
sudo -u postgres pg_restore -d nkeeper --clean --if-exists --no-owner --role=nkeeper /var/backups/nkeeper-2026-10-16.dump
sudo systemctl start nkeeper
```

### IIS

Use `pg_dump.exe` and `pg_restore.exe` from `C:\Program Files\PostgreSQL\17\bin`, with the same options, connecting as `postgres`. Stop the **NKeeper** app pool before restoring.

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

Secrets are never logged. These logs are application logs, not a tamper-proof audit trail; keep them with your normal log retention.

## Database migrations

The schema changes through Entity Framework Core migrations, committed in the repository. All installs apply pending migrations when the API starts. That's `Database:MigrateOnStartup=true`, which the setup scripts and Compose set.

For controlled rollouts:
1. Turn that off (`NKEEPER_MIGRATE_ON_STARTUP=false` in Docker).
2. Apply an idempotent SQL script, generated from the repository as described in [Development](development.md#database-migrations), as a deployment step.

Back up before upgrading. Migrations are designed to be safe, but there's no automatic downgrade.

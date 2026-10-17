# Security

NKeeper holds credentials for the machines it connects to, so it's built to keep them on the server, verify every server it talks to, and limit what each user can reach. It hasn't had an independent security review yet.

- [Accounts and sign-in](#accounts-and-sign-in)
- [Stored secrets](#stored-secrets)
- [Access control](#access-control)
- [Remote sessions](#remote-sessions)
- [Web sessions](#web-sessions)
- [The browser side](#the-browser-side)
- [Deployment](#deployment)
- [Known gaps](#known-gaps)

## Accounts and sign-in

- **Passwords** are hashed by ASP.NET Core Identity (PBKDF2). They need at least 12 characters, with upper case, lower case and a digit. Plain-text passwords are never stored or logged.
- **Generic errors.** A wrong password, an unknown user and a locked-out account all get the same `401`. Unknown users still cost a password-hash computation, so response timing doesn't reveal which accounts exist.
- **Brute-force protection:** sign-in attempts are rate-limited per IP (`429` with `Retry-After`), and accounts are locked for 15 minutes after 5 failures.
- **Server-side sessions.** The cookie is `HttpOnly`, `SameSite=Strict` and `Secure` (outside development), and holds only an encrypted session key. The session itself is a database row, so signing out deletes it and a copied cookie stops working immediately. Nothing authentication-related is kept in `localStorage` or `sessionStorage`.
- **Accounts are re-checked on every request.** Disabling a user, changing their role or resetting their password takes effect at once. It signs them out everywhere and ends their open sessions.
- **No public sign-up.** The first administrator comes from a guarded, create-only bootstrap step, and the setup scripts remove its password from the configuration afterwards.
- **Active Directory sign-in** (optional) binds to a domain controller as the user over LDAPS or StartTLS. Clear-text LDAP is refused unless explicitly allowed. Empty passwords are rejected, so an unauthenticated bind never counts as a sign-in. A domain account can never take over a local account of the same name.

## Stored secrets

- **Write-only encryption.** Credential passwords and private keys are encrypted with ASP.NET Core Data Protection before they reach the database. The API never returns them: responses only say `hasPassword` / `hasPrivateKey`.
- **Domain sign-in passwords** (when RDP pass-through is on) are encrypted the same way and stored on the user's server-side session row, never in the cookie or the browser. Signing out, expiry and revocation delete them with the session. They are used only for RDP sessions the user opens with **My sign-in**.
- **Server-side use only.** Secrets are decrypted in memory at the moment of connecting. **Cloning** a host copies the encrypted values on the server, so they never reach the browser either.
- **Key storage.** The Data Protection keys are stored in PostgreSQL, so restarts keep working. They are **not** themselves encrypted, so the database and its backups are as sensitive as the passwords (see [Known gaps](#known-gaps)).
- **Configuration files** with secrets are readable only by their owners:
  - Docker: `.env`;
  - native Linux: `/etc/nkeeper/nkeeper.env`;
  - IIS: `web.config` and `state.json`.

## Access control

- **Authenticated by default.** Every endpoint requires an authenticated user unless it explicitly opts out (sign-in, health checks).
- **Administrators and users.** Users reach only hosts granted to them (by tag group or individually) at *Use* or *Manage* level. Hosts they can't see answer `404`; hosts they can't manage answer `403` to changes.
- **Sessions go through the same checks.** Every kind of session resolves its host, service and credential through those rules. A credential can only be used with its own host, and a session request can't name another host's credential.
- **Changes are logged** with the administrator who made them.

## Remote sessions

- **The server connects.** Browsers talk only to NKeeper. SSH, RDP, VNC, web and console connections are made by the server.
- **One-off logins** are sent in the first WebSocket message, never in a URL, and are never stored.
- **Trust on first use, then pinning.** SSH host keys, RDP certificates and self-signed HTTPS certificates must be confirmed by the user the first time. A changed key or certificate is refused, not prompted. RDP certificates are probed by the API, and the fingerprint is then pinned in guacd. RDP never falls back to legacy security without TLS.
- **guacd** (RDP/VNC) has no authentication of its own, so it's never published. It's reachable only on the Compose network or `127.0.0.1`, runs with all capabilities dropped, and receives only instructions that pass NKeeper's allow-list filter. Clipboard data is capped. File transfer, drives, printing, microphone and recording are disabled.
- **VNC** has no transport security. The session says "Unencrypted to host".
- **KVM consoles** are tunnelled through the SSH connection to the hypervisor (with its host key verified). Each console belongs to the user who opened it, and closes when idle or after 12 hours.
- **WebSocket hijacking:** browsers attach cookies to cross-site WebSocket handshakes and CORS doesn't apply to them. So session endpoints only accept handshakes whose `Origin` is NKeeper itself (or a configured `Cors:AllowedOrigins` entry).
- **Limits and logging:** each user has session limits. Every session is logged with user, host, credential ID and duration, never secrets.

## Web sessions

Proxying a device's web interface means running someone else's web app, so each session is isolated:

- **Its own origin.** It runs on `nkw-<32 random hex>.<web domain>`, separate from NKeeper's, so its scripts can't read NKeeper's cookies or call NKeeper's API. NKeeper's API is never served on session origins, and NKeeper's cookies are never forwarded to the host.
- **A sandboxed frame** without `allow-top-navigation`, so the app can't navigate NKeeper away. Its framing rules are replaced with "only NKeeper may frame this".
- **A one-time launch link** sets a session cookie in that browser only. The session ends when its tab closes, when idle, at its maximum lifetime, or as soon as the user loses access.
- **A separate registrable domain** (`WebProxy:BaseDomain`, e.g. `nkeeper-web.example.net`) is recommended, so proxied apps can't even set cookies for NKeeper's parent domain.

## The browser side

- **CSRF:**
  - every non-GET request needs an antiforgery token (`X-XSRF-TOKEN`), bound to the user and checked against an `HttpOnly` cookie;
  - with `SameSite=Strict` cookies, that also covers sign-in CSRF.
- **Content Security Policy:**
  - scripts are allowed only from NKeeper itself, and NKeeper can't be framed;
  - inline styles are allowed, because the terminal injects them;
  - frames are allowed for web sessions.
  - nginx sets the policy, or the API sets it under IIS. Also sent: `X-Content-Type-Options`, `Referrer-Policy` and `Permissions-Policy`.
- **CORS** is off unless origins are listed explicitly.
- **Errors** are RFC 7807 problem details with a trace ID. Stack traces are never returned outside development.
- **Scans** need *Manage* access to the host. An internal scan is one SSH login, with the host key verified like any SSH session, running read-only commands. An external scan probes about 35 well-known TCP ports, plus the host's own service ports, on that host's saved address only. At most four external scans run at once. Neither saves anything: findings are applied by the user as a normal host edit.
- **mRemoteNG imports** are for administrators. The file is parsed on the server with DTDs and external entities disabled. Its passwords are decrypted there and stored like any other credential, so they never come back to the browser.
- **Third-party browser code:** the Guacamole client and spice-html5 are vendored from official releases, with provenance recorded. The Guacamole release's signature was verified. noVNC (VNC consoles) is installed from npm, where it's published by the noVNC project itself.

## Deployment

- **HTTPS everywhere.** Every install serves HTTPS, and plain HTTP only redirects. The default certificate comes from a private CA that NKeeper generates per server (ECDSA P-256), whose key never leaves the server. You can use your own certificate instead.
- **Containers** run as non-root. PostgreSQL, the API and guacd publish no ports, and secrets are passed at run time, never baked into images.
- **Native Linux** runs the API as an unprivileged `nkeeper` user with systemd sandboxing (read-only system, no new privileges, no capabilities), listening only on loopback behind nginx. PostgreSQL accepts NKeeper's login on loopback only.
- **IIS** runs NKeeper in its own app pool identity.

## Known gaps

- **Data Protection keys aren't encrypted at rest.** They sit in the same database as the secrets they protect. Encrypting them with a certificate or a key vault is planned. Until then, protect the database and its backups like the credentials themselves, and never lose them: without the keys, stored secrets are unrecoverable.
- **No two-factor sign-in, and no tamper-proof audit trail** yet. Session and account events go to the application log.
- **Web sessions** trust the proxied device's pages to behave like web pages. The isolation above limits what they can reach, but a malicious device page still runs in the user's browser on its own origin.
- **The session cookie lifetime** slides (8 hours, or 14 days with "Remember me"). There's no forced re-authentication for sensitive actions.
- **Rate limiting and session limits are in memory**, per API instance.

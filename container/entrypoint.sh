#!/bin/sh
# Start-up of the single NKeeper image: prepares /var/lib/nkeeper (settings, PostgreSQL, HTTPS
# certificate, first administrator), then runs every service under supervisord.
set -eu

DATA=/var/lib/nkeeper
PGDATA=$DATA/postgres
TLS=$DATA/tls
CONFIG=$DATA/nkeeper.env
ADMIN_FILE=$DATA/initial-admin.txt
PG_BIN=/usr/lib/postgresql/17/bin

log() { echo "nkeeper: $*"; }

mkdir -p "$DATA"
chmod 755 "$DATA"

# --- Settings: the container's own environment wins over nkeeper.env -----------------------------
if [ ! -f "$CONFIG" ]; then
    install -m 600 /etc/nkeeper/nkeeper.env.example "$CONFIG"
    log "wrote $CONFIG; edit it and restart to change settings."
fi
saved=$(export -p)
set -a
# shellcheck disable=SC1090
. "$CONFIG"
set +a
eval "$saved"

# --- PostgreSQL: created on the first start, reached over its Unix socket with peer auth ---------
mkdir -p /run/postgresql /run/nkeeper
chown postgres:postgres /run/postgresql
chmod 2775 /run/postgresql
pg() { runuser -u postgres -- "$@"; }

if [ ! -s "$PGDATA/PG_VERSION" ]; then
    log "creating the database in $PGDATA"
    mkdir -p "$PGDATA"
    chown postgres:postgres "$PGDATA"
    chmod 700 "$PGDATA"
    pg "$PG_BIN/initdb" -D "$PGDATA" -E UTF8 --locale=C.UTF-8 --auth-local=peer --auth-host=scram-sha-256 >/dev/null
    pg "$PG_BIN/pg_ctl" -D "$PGDATA" -o "-c listen_addresses= -c unix_socket_directories=/run/postgresql" -w -s start
    pg "$PG_BIN/psql" -h /run/postgresql -q -v ON_ERROR_STOP=1 -d postgres \
        -c "CREATE ROLE nkeeper LOGIN" -c "CREATE DATABASE nkeeper OWNER nkeeper"
    pg "$PG_BIN/pg_ctl" -D "$PGDATA" -m fast -w -s stop
elif [ "$(cat "$PGDATA/PG_VERSION")" != 17 ]; then
    log "the database in $PGDATA is PostgreSQL $(cat "$PGDATA/PG_VERSION"), but this image runs 17. Move it with Move to a new server (docs/administration.md)." >&2
    exit 1
fi
chown -R postgres:postgres "$PGDATA"

# --- First administrator: until the database has a user, from nkeeper.env or a random password ----
users=0
pg "$PG_BIN/pg_ctl" -D "$PGDATA" -o "-c listen_addresses= -c unix_socket_directories=/run/postgresql" -w -s start
psql_value() { pg "$PG_BIN/psql" -h /run/postgresql -d nkeeper -tAc "$1"; }
# Before the API's first start the table doesn't exist yet.
if [ "$(psql_value "SELECT to_regclass('\"AspNetUsers\"') IS NOT NULL")" = t ]; then
    users=$(psql_value 'SELECT count(*) FROM "AspNetUsers"')
fi
pg "$PG_BIN/pg_ctl" -D "$PGDATA" -m fast -w -s stop

admin_user=${NKEEPER_ADMIN_USERNAME:-admin}
admin_email=${NKEEPER_ADMIN_EMAIL:-admin@nkeeper.local}
admin_password=${NKEEPER_ADMIN_PASSWORD:-}
bootstrap=false
if [ "$users" = 0 ]; then
    bootstrap=true
    if [ -z "$admin_password" ]; then
        if [ -s "$ADMIN_FILE" ]; then
            admin_password=$(sed -n 's/^Password: //p' "$ADMIN_FILE")
        else
            # Meets the policy (12+ characters, upper case, lower case and a digit) by construction.
            admin_password="$(openssl rand -base64 32 | tr -dc 'A-Za-z0-9' | cut -c1-20)Aa1"
            umask 077
            printf 'NKeeper first administrator. Delete this file once you have signed in.\nUser name: %s\nPassword: %s\n' \
                "$admin_user" "$admin_password" >"$ADMIN_FILE"
            umask 022
        fi
    fi
fi

# --- HTTPS: NKeeper's private CA and certificate, or your own custom.crt/custom.key in tls/ -------
names=${NKEEPER_SERVER_NAMES:-}
if [ -z "$names" ]; then
    addresses() { ip -o addr show scope global 2>/dev/null | awk '{ sub(/\/.*/, "", $4); print $4 }' | tr '\n' ' '; }
    # With DHCP (e.g. a Proxmox container) the address can arrive just after start-up.
    tries=0
    while [ -z "$(addresses)" ] && [ "$tries" -lt 20 ]; do
        tries=$((tries + 1))
        sleep 1
    done
    names="$(cat /proc/sys/kernel/hostname) $(addresses)"
fi
# shellcheck disable=SC2086 # names are split on purpose
names=$(echo $names)
web_domain=${NKEEPER_WEB_BASE_DOMAIN:-}
https_port=${NKEEPER_HTTPS_PORT:-443}
mkdir -p "$TLS"
chmod 700 "$TLS"
# shellcheck disable=SC2086
/usr/local/bin/nkeeper-tls.sh certificate "$TLS" $names ${web_domain:+"*.$web_domain"}
/usr/local/bin/nkeeper-tls.sh config /etc/nkeeper/tls.conf.template /etc/nginx/conf.d/nkeeper-tls.conf \
    "$TLS" 443 80 "$https_port" /opt/nkeeper/www "$names"
/usr/sbin/nginx -t -q

# --- The API's settings, for nkeeper-api only -----------------------------------------------------
quote() { printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"; }
umask 077
{
    # Anything else from nkeeper.env or the environment that configures the API (Section__Key).
    env | grep -E '^[A-Za-z][A-Za-z0-9]*__[A-Za-z0-9_]+=' | while IFS='=' read -r key value; do
        echo "$key=$(quote "$value")"
    done
    echo "ASPNETCORE_ENVIRONMENT=Production"
    echo "ASPNETCORE_URLS=http://127.0.0.1:5080"
    echo "ConnectionStrings__NKeeper='Host=/run/postgresql;Database=nkeeper;Username=nkeeper'"
    echo "Database__MigrateOnStartup=true"
    echo "ForwardedHeaders__Enabled=true"
    echo "Guacd__Host=127.0.0.1"
    echo "Guacd__Port=4822"
    echo "WebProxy__BaseDomain=$(quote "$web_domain")"
    if $bootstrap; then
        echo "BootstrapAdmin__Enabled=true"
        echo "BootstrapAdmin__AllowOutsideDevelopment=true"
        echo "BootstrapAdmin__UserName=$(quote "$admin_user")"
        echo "BootstrapAdmin__Email=$(quote "$admin_email")"
        echo "BootstrapAdmin__Password=$(quote "$admin_password")"
    fi
} >/run/nkeeper/api.env
umask 022
chown nkeeper:nkeeper /run/nkeeper /run/nkeeper/api.env

first=${names%% *}
log "open https://$first$([ "$https_port" = 443 ] || echo ":$https_port")/ (also: $names)"
if $bootstrap; then
    if [ -s "$ADMIN_FILE" ] && [ -z "${NKEEPER_ADMIN_PASSWORD:-}" ]; then
        log "first administrator: $admin_user, password in $ADMIN_FILE"
    else
        log "first administrator: $admin_user, with NKEEPER_ADMIN_PASSWORD"
    fi
fi
[ -s "$TLS/custom.crt" ] || log "the certificate is from NKeeper's private CA: install $TLS/ca.crt on client machines to trust it."

exec /usr/bin/supervisord -c /etc/supervisor/supervisord.conf

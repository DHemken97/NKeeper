#!/usr/bin/env bash
# NKeeper setup for Linux. Installs or upgrades NKeeper from this checkout; safe to run again.
#
#   scripts/setup.sh                    # asks a few questions (Docker install by default)
#   scripts/setup.sh --yes              # no questions: Docker, HTTPS on 443 for this machine's name
#   sudo scripts/setup.sh --mode native # systemd service + nginx + PostgreSQL on this machine
#
# Running it again upgrades with the previous answers, and skips what hasn't changed: NKeeper is
# only rebuilt when its source has changed (--rebuild builds it anyway).
#
# Modes
#   docker  (recommended) PostgreSQL, the API, guacd and nginx in Docker Compose containers.
#   native  The API as a systemd service behind the distribution's nginx, with its PostgreSQL.
#           guacd (needed for RDP/VNC) still runs as a container (Docker or Podman), because
#           distributions don't ship guacd 1.6. Debian/Ubuntu (apt) and RHEL/Rocky/Alma/Fedora (dnf).
#
# Run with --help for all options.
set -euo pipefail

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
GUACD_IMAGE=docker.io/guacamole/guacd:1.6.0
NODE_MAJOR=22
NATIVE_PREFIX=/opt/nkeeper
NATIVE_CONFIG=/etc/nkeeper
NATIVE_TLS=/etc/nginx/nkeeper-tls
API_PORT=5080

mode="" names="" web_domain="" local_only=false https_port=443 http_port=80
admin_user="" admin_email="" admin_password="" admin_password_arg="" cert="" key=""
assume_yes=false install_docker=false uninstall=false purge=false no_guacd=false rebuild=false
https_port_set=false http_port_set=false web_domain_set=false

usage() {
    cat <<'EOF'
Usage: scripts/setup.sh [options]

  --mode docker|native     How to install (default: docker)
  --hostname NAMES         Names/addresses clients use, comma-separated
                           (default: this machine's name and IP address)
  --web-domain DOMAIN      Separate domain for web sessions (nkw-<id>.DOMAIN)
  --local-only             Only reachable from this machine (https://localhost)
  --https-port N           HTTPS port (default 443)
  --http-port N            HTTP port that redirects to HTTPS (default 80)
  --cert FILE --key FILE   Your own certificate (PEM, with any intermediates) and key,
                           instead of one from NKeeper's private CA
  --admin-user NAME        First administrator (default: admin)
  --admin-email EMAIL      Their email (default: admin@<hostname>)
  --admin-password PASS    Their password (default: generated and shown once)
  --install-docker         Install Docker if missing (docker mode), without asking
  --no-guacd               Native mode: skip guacd (RDP and VNC won't work)
  --rebuild                Build NKeeper even if its source hasn't changed since the last
                           build (e.g. to pick up .NET, Node.js or base image updates)
  -y, --yes                Don't ask; use the defaults
  --uninstall              Remove NKeeper (keeps the database unless --purge)
  --purge                  With --uninstall: also delete the database and certificates
  -h, --help               This help
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --mode) mode=$2; shift ;;
        --hostname) names=${2//,/ }; shift ;;
        --web-domain) web_domain=$2; web_domain_set=true; shift ;;
        --local-only) local_only=true ;;
        --https-port) https_port=$2; https_port_set=true; shift ;;
        --http-port) http_port=$2; http_port_set=true; shift ;;
        --cert) cert=$2; shift ;;
        --key) key=$2; shift ;;
        --admin-user) admin_user=$2; shift ;;
        --admin-email) admin_email=$2; shift ;;
        --admin-password) admin_password=$2 admin_password_arg=$2; shift ;;
        --install-docker) install_docker=true ;;
        --no-guacd) no_guacd=true ;;
        --rebuild) rebuild=true ;;
        -y|--yes) assume_yes=true ;;
        --uninstall) uninstall=true ;;
        --purge) purge=true ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1 (see --help)" >&2; exit 2 ;;
    esac
    shift
done

# --- Helpers -------------------------------------------------------------------------------------

if [ -t 1 ]; then bold=$'\e[1m' green=$'\e[32m' yellow=$'\e[33m' red=$'\e[31m' reset=$'\e[0m'; else bold="" green="" yellow="" red="" reset=""; fi
step() { echo "${bold}==> $*${reset}"; }
ok() { echo "${green}    $*${reset}"; }
warn() { echo "${yellow}warning: $*${reset}" >&2; }
die() { echo "${red}error: $*${reset}" >&2; exit 1; }

interactive() { ! $assume_yes && [ -t 0 ]; }

# ask "Question" default -> answer (the default when not interactive or left empty)
ask() {
    local answer=""
    if interactive; then
        read -r -p "$1 [$2]: " answer </dev/tty || true
    fi
    echo "${answer:-$2}"
}

confirm() {
    $assume_yes && return 0
    [ -t 0 ] || return 1
    local answer
    read -r -p "$1 [Y/n]: " answer </dev/tty || true
    [[ ! "$answer" =~ ^[Nn] ]]
}

random_text() { head -c 64 /dev/urandom | od -An -tx1 | tr -d ' \n' | head -c "$1"; }

# Meets NKeeper's policy: 12+ characters with upper case, lower case and a digit.
generate_password() { echo "Nk$(random_text 6)-$(random_text 6)-$(random_text 6)A7"; }

valid_password() {
    [ ${#1} -ge 12 ] && [[ "$1" =~ [A-Z] ]] && [[ "$1" =~ [a-z] ]] && [[ "$1" =~ [0-9] ]]
}

default_names() {
    local fqdn ip
    fqdn=$(hostname -f 2>/dev/null || hostname)
    ip=$(hostname -I 2>/dev/null | awk '{print $1}')
    [ -n "$ip" ] || ip=$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{for (i = 1; i < NF; i++) if ($i == "src") print $(i + 1)}')
    echo "$fqdn $ip" | xargs
}

first_name() {
    local name
    for name in $names; do
        if [[ ! "$name" =~ ^[0-9.]+$ && "$name" != *:* ]]; then echo "$name"; return; fi
    done
    echo "${names%% *}"
}

url_for() {
    local host=$1 port=$2
    [[ "$host" == *:* ]] && host="[$host]"
    if [ "$port" = 443 ]; then echo "https://$host"; else echo "https://$host:$port"; fi
}

# set_env FILE KEY VALUE: replaces KEY's line or appends it (values are written verbatim).
set_env() {
    local file=$1 key=$2 value=$3 tmp
    tmp=$(mktemp)
    KEY=$key VALUE=$value awk 'BEGIN { done = 0 }
        index($0, ENVIRON["KEY"] "=") == 1 { print ENVIRON["KEY"] "=" ENVIRON["VALUE"]; done = 1; next }
        { print }
        END { if (!done) print ENVIRON["KEY"] "=" ENVIRON["VALUE"] }' "$file" >"$tmp"
    cat "$tmp" >"$file"
    rm -f "$tmp"
}

get_env() { [ -f "$1" ] && sed -n "s/^$2=//p" "$1" | tail -n 1 || true; }

# source_hash DIR...: one hash of the files under DIRs (of the checkout) that a build uses, to
# tell whether they changed since the last build. Git's file list when it's a Git checkout (so
# ignored output like node_modules doesn't count), else every file but build output.
source_hash() {
    (
        cd "$REPO"
        if command -v git >/dev/null 2>&1 && git -c safe.directory="$REPO" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
            git -c safe.directory="$REPO" ls-files -z --cached --others --exclude-standard -- "$@"
        else
            find "$@" \( -name node_modules -o -name bin -o -name obj -o -name dist -o -name .git \) -prune -o -type f -print0
        fi | sort -z | { xargs -0 sha256sum 2>/dev/null || true; } | sha256sum | cut -c1-64
    )
}

port_in_use() {
    if command -v ss >/dev/null 2>&1; then
        ss -Hltn "sport = :$1" 2>/dev/null | grep -q .
    else
        (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null
    fi
}

wait_for() {
    local url=$1 seconds=$2 i
    for ((i = 0; i < seconds; i += 2)); do
        if curl -fsS -o /dev/null "$url" 2>/dev/null; then return 0; fi
        sleep 2
    done
    return 1
}

# --- Questions -----------------------------------------------------------------------------------

choose_settings() {
    local installed=""
    if [ -f /etc/systemd/system/nkeeper.service ]; then installed=native; elif [ -f "$REPO/.env" ]; then installed=docker; fi
    if [ -z "$mode" ]; then
        if [ -n "$installed" ]; then
            mode=$installed
        elif interactive; then
            echo "How should NKeeper run?"
            echo "  1) Docker containers (recommended)"
            echo "  2) Native: systemd service + nginx + PostgreSQL on this machine"
            case "$(ask "Choose" 1)" in 2|native) mode=native ;; *) mode=docker ;; esac
        else
            mode=docker
        fi
    fi
    [[ "$mode" == docker || "$mode" == native ]] || die "--mode must be docker or native"

    # A re-run keeps the previous answers unless options override them.
    local previous="" previous_https="" previous_http="" previous_bind="" previous_web=""
    if [ "$mode" = docker ]; then
        previous=$(get_env "$REPO/.env" NKEEPER_SERVER_NAMES)
        previous_https=$(get_env "$REPO/.env" NKEEPER_HTTPS_PORT)
        previous_http=$(get_env "$REPO/.env" NKEEPER_HTTP_REDIRECT_PORT)
        previous_bind=$(get_env "$REPO/.env" NKEEPER_HTTPS_BIND)
        previous_web=$(get_env "$REPO/.env" NKEEPER_WEB_BASE_DOMAIN)
        # .env.example's values (localhost on 8443) aren't a previous choice.
        if [ "$previous" = localhost ] && [ "$previous_https" = 8443 ]; then previous="" previous_https="" previous_http=""; fi
    else
        previous=$(get_env "$NATIVE_CONFIG/nkeeper.env" NKEEPER_SERVER_NAMES | tr -d '"')
        previous_https=$(get_env "$NATIVE_CONFIG/nkeeper.env" NKEEPER_HTTPS_PORT)
        previous_http=$(get_env "$NATIVE_CONFIG/nkeeper.env" NKEEPER_HTTP_PORT)
        previous_web=$(get_env "$NATIVE_CONFIG/nkeeper.env" WebProxy__BaseDomain)
        [ "$previous" = localhost ] && previous_bind=127.0.0.1
    fi
    $https_port_set || https_port=${previous_https:-443}
    $http_port_set || http_port=${previous_http:-80}
    $web_domain_set || web_domain=$previous_web
    # A local-only install stays local-only unless new names are given.
    if [ -z "$names" ] && [ "$previous" = localhost ] && [ "$previous_bind" = 127.0.0.1 ]; then local_only=true; fi

    if $local_only; then
        names=localhost
    elif [ -z "$names" ] && [ -n "$previous" ]; then
        # Already installed: keep its names rather than asking again.
        names=$previous
        ok "Keeping the names NKeeper is opened with: $names (--hostname changes them)."
    elif [ -z "$names" ]; then
        names=$(ask "Names or addresses people will use to open NKeeper (space-separated)" "$(default_names)")
    fi
    [ -n "$names" ] || die "No host name given (use --hostname)"
    if { [ -n "$cert" ] && [ -z "$key" ]; } || { [ -z "$cert" ] && [ -n "$key" ]; }; then die "--cert and --key go together"; fi
    if [ -n "$cert" ]; then
        [ -r "$cert" ] && [ -r "$key" ] || die "Can't read $cert or $key"
        cert=$(cd "$(dirname "$cert")" && pwd)/$(basename "$cert")
        key=$(cd "$(dirname "$key")" && pwd)/$(basename "$key")
    fi
}

choose_admin() {
    admin_user=${admin_user:-$(ask "First administrator's user name" admin)}
    local domain
    domain=$(first_name)
    [[ "$domain" == *.* ]] || domain="$domain.local"
    admin_email=${admin_email:-$(ask "Their email address" "admin@$domain")}
    local problem
    while true; do
        if [ -z "$admin_password" ] && interactive; then
            read -r -s -p "Their password (12+ characters, upper and lower case and a digit; empty = generate): " admin_password </dev/tty || true
            echo
        fi
        if [ -z "$admin_password" ]; then
            admin_password=$(generate_password)
            generated_password=true
        fi
        problem=$(password_problem "$admin_password")
        [ -z "$problem" ] && return
        # Ask again rather than stopping halfway through the install.
        interactive && [ -z "$admin_password_arg" ] || die "$problem"
        warn "$problem"
        admin_password=""
    done
}

password_problem() {
    if ! valid_password "$1"; then
        echo "The password needs 12+ characters with upper case, lower case and a digit."
    elif [ "$mode" = native ] && [[ "$1" == *[\"\\\$\`]* ]]; then
        echo "In native mode the password can't contain \" \\ \$ or \`."
    fi
}

generated_password=false
bootstrapped=false

# --- Docker --------------------------------------------------------------------------------------

DOCKER=(docker)

ensure_docker() {
    if ! command -v docker >/dev/null 2>&1; then
        if $install_docker || confirm "Docker isn't installed. Install it now with Docker's official script (get.docker.com)?"; then
            step "Installing Docker"
            curl -fsSL https://get.docker.com -o /tmp/get-docker.sh
            if [ "$(id -u)" -eq 0 ]; then sh /tmp/get-docker.sh; else sudo sh /tmp/get-docker.sh; fi
            rm -f /tmp/get-docker.sh
            if [ "$(id -u)" -eq 0 ]; then systemctl enable --now docker; else sudo systemctl enable --now docker; fi
        else
            die "Docker is required for --mode docker. Install it (https://docs.docker.com/engine/install/) or use --mode native."
        fi
    fi
    if ! docker info >/dev/null 2>&1; then
        [ "$(id -u)" -eq 0 ] || DOCKER=(sudo docker)
        "${DOCKER[@]}" info >/dev/null 2>&1 || die "Docker isn't running or you can't use it. Start it (sudo systemctl start docker) or add yourself to the docker group."
    fi
    "${DOCKER[@]}" compose version >/dev/null 2>&1 || die "Docker Compose v2 is missing. Install the docker-compose-plugin package."
}

compose() { (cd "$REPO" && "${DOCKER[@]}" compose "$@"); }

docker_install() {
    ensure_docker
    local env="$REPO/.env" fresh=true
    "${DOCKER[@]}" volume inspect nkeeper_postgres-data >/dev/null 2>&1 && fresh=false

    step "Writing $env"
    [ -f "$env" ] || cp "$REPO/.env.example" "$env"
    chmod 600 "$env"
    if [ -z "$(get_env "$env" POSTGRES_PASSWORD)" ]; then
        $fresh || die "A database already exists but .env has no POSTGRES_PASSWORD. Restore .env, or start over with --uninstall --purge."
        set_env "$env" POSTGRES_PASSWORD "$(random_text 48)"
    fi
    # A run that failed before NKeeper started never created the administrator: ask again.
    [ "$(get_env "$env" NKEEPER_BOOTSTRAP_ADMIN_ENABLED)" = true ] && fresh=true
    local bind=0.0.0.0
    $local_only && bind=127.0.0.1
    set_env "$env" NKEEPER_HTTPS_BIND "$bind"
    set_env "$env" NKEEPER_HTTPS_PORT "$https_port"
    set_env "$env" NKEEPER_HTTP_REDIRECT_PORT "$http_port"
    set_env "$env" NKEEPER_SERVER_NAMES "$names"
    set_env "$env" NKEEPER_WEB_BASE_DOMAIN "$web_domain"
    if $fresh; then
        choose_admin
        set_env "$env" NKEEPER_BOOTSTRAP_ADMIN_ENABLED true
        set_env "$env" NKEEPER_BOOTSTRAP_ADMIN_ALLOW_OUTSIDE_DEVELOPMENT true
        set_env "$env" NKEEPER_BOOTSTRAP_ADMIN_USERNAME "$admin_user"
        set_env "$env" NKEEPER_BOOTSTRAP_ADMIN_EMAIL "$admin_email"
        set_env "$env" NKEEPER_BOOTSTRAP_ADMIN_PASSWORD "$admin_password"
        bootstrapped=true
    else
        ok "Existing installation found: upgrading it (data and accounts are kept)."
    fi

    if [ -z "$(compose ps -q frontend 2>/dev/null)" ]; then
        local port
        for port in "$https_port" "$http_port" "$(get_env "$env" NKEEPER_HTTP_PORT)"; do
            port_in_use "${port:-8080}" && die "Port ${port:-8080} is already in use on this machine. Free it, or choose others with --https-port/--http-port."
        done
    fi

    # Rebuild the images only when what they're built from changed (compose up leaves unchanged containers running).
    local source
    source="$(source_hash backend frontend)-$(get_env "$env" VITE_API_BASE_URL)"
    if ! $rebuild && [ "$(get_env "$env" NKEEPER_BUILT_SOURCE)" = "$source" ] &&
        "${DOCKER[@]}" image inspect nkeeper-backend:local nkeeper-frontend:local >/dev/null 2>&1; then
        step "Starting the containers (NKeeper hasn't changed since its last build, so it isn't rebuilt)"
        compose up -d --remove-orphans
    else
        step "Building and starting the containers (the first build takes a few minutes)"
        compose up -d --build --remove-orphans
        set_env "$env" NKEEPER_BUILT_SOURCE "$source"
    fi

    if [ -n "$cert" ]; then
        step "Installing your certificate"
        compose cp "$cert" frontend:/etc/nginx/tls/custom.crt
        compose cp "$key" frontend:/etc/nginx/tls/custom.key
        compose exec -T -u 0 frontend sh -c 'chown 101:101 /etc/nginx/tls/custom.* && chmod 644 /etc/nginx/tls/custom.crt && chmod 600 /etc/nginx/tls/custom.key'
        compose restart frontend
    fi

    step "Waiting for NKeeper to start"
    local local_port
    local_port=$(get_env "$env" NKEEPER_HTTP_PORT)
    if ! wait_for "http://127.0.0.1:${local_port:-8080}/api/auth/antiforgery" 240; then
        compose logs --tail 40 backend >&2 || true
        die "NKeeper didn't start in time (logs above). Check: docker compose logs backend"
    fi
    ok "NKeeper is up."

    if $bootstrapped; then
        # The administrator now exists: stop passing their password to the API.
        set_env "$env" NKEEPER_BOOTSTRAP_ADMIN_ENABLED false
        set_env "$env" NKEEPER_BOOTSTRAP_ADMIN_PASSWORD ""
        compose up -d backend >/dev/null 2>&1
    fi

    if [ -z "$cert" ] && compose exec -T frontend test ! -s /etc/nginx/tls/custom.crt 2>/dev/null; then
        compose cp frontend:/etc/nginx/tls/ca.crt "$REPO/nkeeper-ca.crt" >/dev/null 2>&1 && ca_file="$REPO/nkeeper-ca.crt"
        [ -n "${SUDO_USER:-}" ] && [ -n "$ca_file" ] && chown "$SUDO_USER" "$ca_file" || true
    fi
    summary
}

docker_uninstall() {
    ensure_docker
    step "Removing NKeeper's containers"
    if $purge; then
        compose down -v --remove-orphans
        rm -f "$REPO/.env" "$REPO/nkeeper-ca.crt"
        ok "Containers, database and certificates deleted."
    else
        compose down --remove-orphans
        ok "Containers removed. The database is kept (volume nkeeper_postgres-data); --purge deletes it."
    fi
}

# --- Native --------------------------------------------------------------------------------------

pm=""

# apt_installed PACKAGE...: whether each one (a name or a dpkg pattern) is installed.
apt_installed() {
    local package
    for package in "$@"; do
        dpkg-query -W -f='${db:Status-Abbrev}\n' "$package" 2>/dev/null | grep -q '^ii' || return 1
    done
}

native_packages() {
    local apt_packages=(postgresql nginx openssl curl ca-certificates tar xz-utils python3 python3-venv)
    # No curl package: RHEL 9's curl-minimal (already present, since this script needs curl) conflicts with it.
    local dnf_packages=(postgresql-server nginx openssl ca-certificates tar xz libicu python3 policycoreutils-python-utils)
    if command -v apt-get >/dev/null 2>&1; then
        pm=apt
        export DEBIAN_FRONTEND=noninteractive
        if apt_installed "${apt_packages[@]}" 'libicu[0-9]*'; then
            ok "Already installed."
            return
        fi
        apt-get update -qq
        local icu
        icu=$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{print $1}' | sort -V | tail -n 1)
        apt-get install -y -qq "${apt_packages[@]}" ${icu:+"$icu"} >/dev/null
    elif command -v dnf >/dev/null 2>&1; then
        pm=dnf
        if rpm -q "${dnf_packages[@]}" >/dev/null 2>&1; then
            ok "Already installed."
            return
        fi
        dnf install -y -q "${dnf_packages[@]}" >/dev/null
    else
        die "Native mode supports apt (Debian/Ubuntu) and dnf (RHEL/Rocky/Alma/Fedora). Use --mode docker instead."
    fi
}

# Impacket reads Windows hosts over DCOM/WMI for the RPC internal scan, as in the Docker image. The
# API looks for it at /opt/impacket (NKEEPER_WMI_PYTHON overrides). Optional: without it, only
# Windows scans fail, so a failed download is a warning.
native_impacket() {
    step "Installing Impacket (Windows scans)"
    if [ "$(/opt/impacket/bin/python3 -c 'import importlib.metadata as m; print(m.version("impacket"))' 2>/dev/null)" = 0.12.0 ]; then
        ok "Impacket 0.12.0 is already in /opt/impacket."
        return
    fi
    if [ ! -x /opt/impacket/bin/python3 ] && ! python3 -m venv /opt/impacket >/dev/null 2>&1; then
        warn "Couldn't create /opt/impacket; scanning Windows hosts won't work."
        return
    fi
    if /opt/impacket/bin/pip install -q --disable-pip-version-check impacket==0.12.0 >/dev/null 2>&1; then
        ok "Impacket 0.12.0 in /opt/impacket"
    else
        warn "Couldn't install Impacket (no internet access?); scanning Windows hosts won't work."
    fi
}

native_postgres() {
    step "Setting up PostgreSQL"
    if [ "$pm" = dnf ] && [ ! -s /var/lib/pgsql/data/PG_VERSION ]; then
        postgresql-setup --initdb >/dev/null
    fi
    systemctl enable --now postgresql >/dev/null 2>&1
    wait_postgres

    local password
    password=$(get_env "$NATIVE_CONFIG/nkeeper.env" NKEEPER_DB_PASSWORD)
    password=${password:-$(random_text 48)}
    db_password=$password
    postgres_psql -q -v ON_ERROR_STOP=1 -d postgres <<SQL
SET password_encryption = 'scram-sha-256';
DO \$\$ BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'nkeeper') THEN CREATE ROLE nkeeper LOGIN; END IF;
END \$\$;
ALTER ROLE nkeeper WITH LOGIN PASSWORD '$password';
SELECT 'CREATE DATABASE nkeeper OWNER nkeeper' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'nkeeper')\gexec
SQL

    # Password sign-in for NKeeper over loopback (RHEL's default is "ident"), ahead of other rules.
    local hba
    hba=$(postgres_psql -tAc 'SHOW hba_file')
    if ! grep -q 'NKeeper' "$hba"; then
        local tmp
        tmp=$(mktemp)
        { echo "# NKeeper (added by scripts/setup.sh)"
          echo "host    nkeeper    nkeeper    127.0.0.1/32    scram-sha-256"
          echo "host    nkeeper    nkeeper    ::1/128         scram-sha-256"
          cat "$hba"; } >"$tmp"
        cat "$tmp" >"$hba"
        rm -f "$tmp"
        postgres_psql -qc 'SELECT pg_reload_conf()' >/dev/null
    fi
    ok "Database nkeeper ready."
}

# psql as the postgres user, from / (it can't enter the caller's home directory).
postgres_psql() { (cd / && runuser -u postgres -- psql "$@"); }

wait_postgres() {
    local i
    for ((i = 0; i < 30; i++)); do
        postgres_psql -qtAc 'SELECT 1' >/dev/null 2>&1 && return 0
        sleep 1
    done
    die "PostgreSQL didn't start (systemctl status postgresql)."
}

native_toolchain() {
    step "Fetching the build tools (.NET SDK 10, Node.js $NODE_MAJOR) into /var/cache/nkeeper-build"
    local cache=/var/cache/nkeeper-build arch node_arch
    mkdir -p "$cache"
    case "$(uname -m)" in
        x86_64) arch=x64 node_arch=x64 ;;
        aarch64|arm64) arch=arm64 node_arch=arm64 ;;
        *) die "Unsupported CPU architecture: $(uname -m)" ;;
    esac
    rid="linux-$arch"

    if [ ! -x "$cache/dotnet/dotnet" ] || ! "$cache/dotnet/dotnet" --list-sdks | grep -q '^10\.'; then
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$cache/dotnet-install.sh"
        bash "$cache/dotnet-install.sh" --channel 10.0 --install-dir "$cache/dotnet" --no-path >/dev/null
    fi
    dotnet_bin="$cache/dotnet/dotnet"

    if [ ! -x "$cache/node/bin/node" ] || [[ "$("$cache/node/bin/node" --version)" != "v$NODE_MAJOR."* ]]; then
        local base="https://nodejs.org/dist/latest-v$NODE_MAJOR.x" file sum
        curl -fsSL "$base/SHASUMS256.txt" -o "$cache/node-sums.txt"
        file=$(awk -v suffix="-linux-$node_arch.tar.xz" '$2 ~ suffix"$" {print $2}' "$cache/node-sums.txt")
        [ -n "$file" ] || die "Couldn't find a Node.js $NODE_MAJOR download for linux-$node_arch."
        curl -fsSL "$base/$file" -o "$cache/$file"
        sum=$(awk -v f="$file" '$2 == f {print $1}' "$cache/node-sums.txt")
        echo "$sum  $cache/$file" | sha256sum -c --quiet - || die "Node.js download failed its checksum."
        rm -rf "$cache/node" && mkdir -p "$cache/node"
        tar -xJf "$cache/$file" -C "$cache/node" --strip-components=1
        rm -f "$cache/$file"
    fi
    node_bin_dir="$cache/node/bin"
    build_dir="$cache/src"
}

# The source each installed part was built from ($NATIVE_PREFIX/.source-api, .source-frontend),
# so a re-run only rebuilds what changed.
api_source="" frontend_source="" installed_new=false

native_build() {
    api_source=$(source_hash backend)
    frontend_source=$(source_hash frontend)
    local build_api=true build_frontend=true
    if ! $rebuild; then
        [ -x "$NATIVE_PREFIX/api/NKeeper.Api" ] && [ "$(cat "$NATIVE_PREFIX/.source-api" 2>/dev/null)" = "$api_source" ] && build_api=false
        [ -s "$NATIVE_PREFIX/frontend/index.html" ] && [ "$(cat "$NATIVE_PREFIX/.source-frontend" 2>/dev/null)" = "$frontend_source" ] && build_frontend=false
    fi
    rm -rf "$NATIVE_PREFIX/api.new" "$NATIVE_PREFIX/frontend.new"
    if ! $build_api && ! $build_frontend; then
        step "NKeeper hasn't changed since its last build, so it isn't rebuilt (--rebuild builds it anyway)"
        return
    fi

    local what="API and web interface"
    $build_api || what="web interface"
    $build_frontend || what=API
    native_toolchain
    step "Building NKeeper's $what (a few minutes the first time)"
    # Build from a copy, so the checkout doesn't fill with root-owned build output.
    rm -rf "$build_dir" && mkdir -p "$build_dir"
    (cd "$REPO" && tar --exclude=node_modules --exclude=bin --exclude=obj --exclude=dist --exclude=.git -cf - backend frontend) | tar -xf - -C "$build_dir"

    mkdir -p "$NATIVE_PREFIX"
    # Readable by the nkeeper user and nginx even when root's umask is strict.
    chmod 755 "$NATIVE_PREFIX"
    if $build_frontend; then
        (cd "$build_dir/frontend" && PATH="$node_bin_dir:$PATH" npm ci --no-audit --no-fund --loglevel=error && PATH="$node_bin_dir:$PATH" npm run build --silent) >/dev/null
        # Not --preserve=context: SELinux labels from the build cache would stop systemd running the API.
        cp -R --preserve=mode,timestamps "$build_dir/frontend/dist" "$NATIVE_PREFIX/frontend.new"
    fi
    if $build_api; then
        DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "$dotnet_bin" publish "$build_dir/backend/src/NKeeper.Api/NKeeper.Api.csproj" \
            -c Release -r "$rid" --self-contained -o "$build_dir/api" -p:DebugType=none >/dev/null
        cp -R --preserve=mode,timestamps "$build_dir/api" "$NATIVE_PREFIX/api.new"
    fi
    local part
    for part in api.new frontend.new; do
        [ -d "$NATIVE_PREFIX/$part" ] || continue
        chown -R root:root "$NATIVE_PREFIX/$part"
        chmod -R a+rX "$NATIVE_PREFIX/$part"
    done
    ok "Built."
}

native_install_files() {
    [ -d "$NATIVE_PREFIX/api.new" ] || [ -d "$NATIVE_PREFIX/frontend.new" ] || return 0
    systemctl stop nkeeper >/dev/null 2>&1 || true
    if [ -d "$NATIVE_PREFIX/api.new" ]; then
        rm -rf "$NATIVE_PREFIX/api"
        mv "$NATIVE_PREFIX/api.new" "$NATIVE_PREFIX/api"
        echo "$api_source" >"$NATIVE_PREFIX/.source-api"
    fi
    if [ -d "$NATIVE_PREFIX/frontend.new" ]; then
        rm -rf "$NATIVE_PREFIX/frontend"
        mv "$NATIVE_PREFIX/frontend.new" "$NATIVE_PREFIX/frontend"
        echo "$frontend_source" >"$NATIVE_PREFIX/.source-frontend"
    fi
    installed_new=true
    if command -v selinuxenabled >/dev/null 2>&1 && selinuxenabled; then
        # The API binary as a program systemd may start; the rest keeps /opt's default label.
        semanage fcontext -a -t bin_t "$NATIVE_PREFIX/api/NKeeper\.Api" 2>/dev/null ||
            semanage fcontext -m -t bin_t "$NATIVE_PREFIX/api/NKeeper\.Api"
        restorecon -R "$NATIVE_PREFIX/api"
    fi
}

native_config() {
    step "Configuring the NKeeper service"
    id nkeeper >/dev/null 2>&1 || useradd --system --home-dir "$NATIVE_PREFIX" --shell /usr/sbin/nologin nkeeper
    mkdir -p "$NATIVE_CONFIG"
    local env="$NATIVE_CONFIG/nkeeper.env" fresh=true before
    before=$(cat "$env" /etc/systemd/system/nkeeper.service 2>/dev/null | sha256sum)
    # NKEEPER_INSTALLED is only set once NKeeper has started (and created the administrator), so a
    # run that stopped earlier asks for the administrator again.
    [ -n "$(get_env "$env" NKEEPER_INSTALLED)" ] && fresh=false
    touch "$env"
    chown root:root "$env"
    chmod 600 "$env"

    set_env "$env" NKEEPER_DB_PASSWORD "$db_password"
    set_env "$env" ConnectionStrings__NKeeper "\"Host=127.0.0.1;Port=5432;Database=nkeeper;Username=nkeeper;Password=$db_password\""
    set_env "$env" ASPNETCORE_ENVIRONMENT Production
    set_env "$env" ASPNETCORE_URLS "http://127.0.0.1:$API_PORT"
    set_env "$env" Database__MigrateOnStartup true
    set_env "$env" ForwardedHeaders__Enabled true
    set_env "$env" Guacd__Host 127.0.0.1
    set_env "$env" Guacd__Port 4822
    set_env "$env" WebProxy__BaseDomain "$web_domain"
    set_env "$env" DOTNET_CLI_TELEMETRY_OPTOUT 1
    set_env "$env" NKEEPER_SERVER_NAMES "\"$names\""
    set_env "$env" NKEEPER_HTTPS_PORT "$https_port"
    set_env "$env" NKEEPER_HTTP_PORT "$http_port"

    # The certificate that encrypts the keys NKeeper stores in the database, so a copy of the
    # database alone can't decrypt the stored credentials. The API can read it but not change it.
    local keyring="$NATIVE_CONFIG/data-protection.pem"
    if [ ! -s "$keyring" ]; then
        (umask 077 && openssl req -x509 -newkey rsa:3072 -nodes -days 36500 -subj "/CN=NKeeper Data Protection" \
            -keyout "$keyring.key" -out "$keyring.crt" 2>/dev/null && cat "$keyring.crt" "$keyring.key" >"$keyring") ||
            die "Couldn't generate $keyring."
        rm -f "$keyring.crt" "$keyring.key"
        ok "Generated $keyring: back it up apart from the database. Without it, stored credentials can't be decrypted."
    fi
    chown root:nkeeper "$keyring"
    chmod 640 "$keyring"
    set_env "$env" DataProtection__CertificatePath "$keyring"

    if $fresh; then
        choose_admin
        set_env "$env" BootstrapAdmin__Enabled true
        set_env "$env" BootstrapAdmin__AllowOutsideDevelopment true
        set_env "$env" BootstrapAdmin__UserName "\"$admin_user\""
        set_env "$env" BootstrapAdmin__Email "\"$admin_email\""
        set_env "$env" BootstrapAdmin__Password "\"$admin_password\""
        bootstrapped=true
    else
        ok "Existing installation found: upgrading it (data and accounts are kept)."
    fi

    cat >/etc/systemd/system/nkeeper.service <<EOF
[Unit]
Description=NKeeper remote connection manager
After=network-online.target postgresql.service
Wants=network-online.target postgresql.service

[Service]
Type=simple
User=nkeeper
Group=nkeeper
WorkingDirectory=$NATIVE_PREFIX/api
EnvironmentFile=$env
ExecStart=$NATIVE_PREFIX/api/NKeeper.Api
Restart=always
RestartSec=5
KillSignal=SIGINT
TimeoutStopSec=30
# Hardening: the API needs only the network and its own files.
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictSUIDSGID=true
LockPersonality=true
CapabilityBoundingSet=

[Install]
WantedBy=multi-user.target
EOF
    systemctl daemon-reload
    [ "$(cat "$env" /etc/systemd/system/nkeeper.service | sha256sum)" = "$before" ] || config_changed=true
}

config_changed=false

# guacd_check WHERE LOG_COMMAND: waits for guacd to listen on 4822, or warns.
guacd_check() {
    local i
    for ((i = 0; i < 20; i++)); do
        if port_in_use 4822; then ok "guacd runs in $1."; return; fi
        sleep 1
    done
    warn "guacd didn't start in $1, so RDP and VNC sessions won't work. See: $2"
}

native_guacd() {
    $no_guacd && { warn "Skipping guacd: RDP and VNC sessions won't work."; return; }
    step "Starting guacd (for RDP and VNC) in a container on 127.0.0.1:4822"
    if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
        if [ "$(docker inspect -f '{{.Config.Image}} {{.State.Running}}' nkeeper-guacd 2>/dev/null)" = "$GUACD_IMAGE true" ]; then
            ok "guacd already runs in Docker (container nkeeper-guacd)."
            return
        fi
        docker rm -f nkeeper-guacd >/dev/null 2>&1 || true
        docker run -d --name nkeeper-guacd --restart unless-stopped -p 127.0.0.1:4822:4822 \
            --cap-drop ALL --security-opt no-new-privileges "$GUACD_IMAGE" >/dev/null
        guacd_check "Docker (container nkeeper-guacd)" "docker logs --tail 20 nkeeper-guacd"
        return
    fi
    if ! command -v podman >/dev/null 2>&1; then
        if [ "$pm" = apt ]; then apt-get install -y -qq podman >/dev/null; else dnf install -y -q podman >/dev/null; fi
    fi
    podman image exists "$GUACD_IMAGE" || podman pull -q "$GUACD_IMAGE" >/dev/null
    local unit=/etc/systemd/system/nkeeper-guacd.service before=""
    before=$(cat "$unit" 2>/dev/null || true)
    cat >"$unit" <<EOF
[Unit]
Description=guacd for NKeeper (RDP and VNC), in a Podman container
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=$(command -v podman) run --rm --replace --name nkeeper-guacd -p 127.0.0.1:4822:4822 --cap-drop ALL --security-opt no-new-privileges $GUACD_IMAGE
ExecStop=$(command -v podman) stop -t 10 nkeeper-guacd
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
    if [ "$(cat "$unit")" = "$before" ] && systemctl is-active -q nkeeper-guacd; then
        ok "guacd already runs in Podman (service nkeeper-guacd)."
        return
    fi
    systemctl daemon-reload
    systemctl enable nkeeper-guacd >/dev/null 2>&1
    systemctl restart nkeeper-guacd
    guacd_check "Podman (service nkeeper-guacd)" "journalctl -u nkeeper-guacd"
}

native_nginx() {
    step "Configuring nginx with HTTPS"
    local nginx_dir=/etc/nginx listen_https listen_http
    mkdir -p "$nginx_dir/snippets/nkeeper" "$nginx_dir/conf.d" "$NATIVE_TLS"
    cp "$REPO"/frontend/nginx/snippets/*.conf "$nginx_dir/snippets/nkeeper/"
    install -m 755 "$REPO/frontend/nginx/nkeeper-tls.sh" "$NATIVE_CONFIG/nkeeper-tls.sh"
    install -m 644 "$REPO/frontend/nginx/tls.conf.template" "$NATIVE_CONFIG/tls.conf.template"
    chmod 700 "$NATIVE_TLS"

    if [ -n "$cert" ]; then
        install -m 644 "$cert" "$NATIVE_TLS/custom.crt"
        install -m 600 "$key" "$NATIVE_TLS/custom.key"
    fi
    # shellcheck disable=SC2086 # names are split on purpose
    "$NATIVE_CONFIG/nkeeper-tls.sh" certificate "$NATIVE_TLS" $names ${web_domain:+"*.$web_domain"}

    if $local_only; then
        listen_https="127.0.0.1:$https_port" listen_http="127.0.0.1:$http_port"
    else
        listen_https=$https_port listen_http=$http_port
    fi
    "$NATIVE_CONFIG/nkeeper-tls.sh" config "$NATIVE_CONFIG/tls.conf.template" "$nginx_dir/conf.d/nkeeper-tls.conf" \
        "$NATIVE_TLS" "$listen_https" "$listen_http" "$https_port" "$NATIVE_PREFIX/frontend" "$names"
    cat >"$nginx_dir/conf.d/nkeeper.conf" <<EOF
# NKeeper (written by scripts/setup.sh; HTTPS servers are in nkeeper-tls.conf).
include snippets/nkeeper/maps.conf;

upstream nkeeper_api {
    server 127.0.0.1:$API_PORT;
}
EOF

    if command -v selinuxenabled >/dev/null 2>&1 && selinuxenabled; then
        setsebool -P httpd_can_network_connect 1
        semanage fcontext -a -t httpd_sys_content_t "$NATIVE_PREFIX/frontend(/.*)?" 2>/dev/null || true
        restorecon -R "$NATIVE_PREFIX/frontend" "$NATIVE_TLS" >/dev/null
        local port
        for port in "$https_port" "$http_port"; do
            semanage port -a -t http_port_t -p tcp "$port" 2>/dev/null || true
        done
    fi

    nginx -t -q || die "nginx rejected the configuration (see above)."
    systemctl enable nginx >/dev/null 2>&1
    # A reload keeps open sessions; it starts nginx if it isn't running.
    systemctl reload-or-restart nginx
}

native_firewall() {
    $local_only && return
    if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
        firewall-cmd -q --permanent --add-port="$https_port/tcp" --add-port="$http_port/tcp"
        firewall-cmd -q --reload
        ok "Opened ports $https_port and $http_port in firewalld."
    elif command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q 'Status: active'; then
        ufw allow "$https_port/tcp" >/dev/null
        ufw allow "$http_port/tcp" >/dev/null
        ok "Opened ports $https_port and $http_port in ufw."
    fi
}

native_install() {
    [ "$(id -u)" -eq 0 ] || die "Native mode installs system services: run it with sudo."
    step "Installing packages (PostgreSQL, nginx, OpenSSL)"
    native_packages
    native_postgres
    native_toolchain
    native_build
    native_config
    native_install_files
    native_impacket
    native_guacd
    native_nginx
    native_firewall

    systemctl enable nkeeper >/dev/null 2>&1
    if $installed_new || $config_changed || ! systemctl is-active -q nkeeper; then
        step "Starting NKeeper"
        systemctl restart nkeeper
    else
        step "NKeeper is unchanged and running, so it isn't restarted"
    fi
    if ! wait_for "http://127.0.0.1:$API_PORT/health/ready" 120; then
        journalctl -u nkeeper -n 40 --no-pager >&2 || true
        die "NKeeper didn't start in time (log above). Check: journalctl -u nkeeper"
    fi
    set_env "$NATIVE_CONFIG/nkeeper.env" NKEEPER_INSTALLED 1
    if $bootstrapped; then
        set_env "$NATIVE_CONFIG/nkeeper.env" BootstrapAdmin__Enabled false
        set_env "$NATIVE_CONFIG/nkeeper.env" BootstrapAdmin__Password '""'
        systemctl restart nkeeper
        wait_for "http://127.0.0.1:$API_PORT/health/ready" 120 || true
    fi
    ok "NKeeper is up."

    if [ ! -s "$NATIVE_TLS/custom.crt" ]; then
        install -m 644 "$NATIVE_TLS/ca.crt" "$REPO/nkeeper-ca.crt"
        [ -n "${SUDO_USER:-}" ] && chown "$SUDO_USER" "$REPO/nkeeper-ca.crt" || true
        ca_file="$REPO/nkeeper-ca.crt"
    fi
    summary
}

native_uninstall() {
    [ "$(id -u)" -eq 0 ] || die "Run it with sudo."
    step "Removing NKeeper's services and files"
    systemctl disable --now nkeeper nkeeper-guacd >/dev/null 2>&1 || true
    command -v docker >/dev/null 2>&1 && docker rm -f nkeeper-guacd >/dev/null 2>&1 || true
    rm -f /etc/systemd/system/nkeeper.service /etc/systemd/system/nkeeper-guacd.service
    systemctl daemon-reload
    rm -rf "$NATIVE_PREFIX" /opt/impacket /var/cache/nkeeper-build /etc/nginx/conf.d/nkeeper.conf /etc/nginx/conf.d/nkeeper-tls.conf /etc/nginx/snippets/nkeeper
    systemctl reload nginx >/dev/null 2>&1 || true
    if $purge; then
        postgres_psql -qc 'DROP DATABASE IF EXISTS nkeeper' -c 'DROP ROLE IF EXISTS nkeeper' >/dev/null 2>&1 || true
        rm -rf "$NATIVE_CONFIG" "$NATIVE_TLS" "$REPO/nkeeper-ca.crt"
        userdel nkeeper >/dev/null 2>&1 || true
        ok "Removed, including the database and certificates."
    else
        ok "Removed. The database (nkeeper) and $NATIVE_CONFIG are kept; --purge deletes them."
    fi
}

# --- Summary -------------------------------------------------------------------------------------

ca_file=""
db_password=""

summary() {
    local name url
    name=$(first_name)
    url=$(url_for "$name" "$https_port")
    echo
    echo "${bold}${green}NKeeper is ready.${reset}"
    echo
    echo "  Open:        ${bold}$url${reset}"
    for name in $names; do
        [ "$name" = "$(first_name)" ] || echo "               $(url_for "$name" "$https_port")"
    done
    if $bootstrapped; then
        echo "  Sign in as:  ${bold}$admin_user${reset}"
        if $generated_password; then
            echo "  Password:    ${bold}$admin_password${reset}   (shown only now: store it, or change it after signing in)"
        fi
    fi
    if [ -n "$ca_file" ]; then
        echo
        echo "  HTTPS uses a certificate from NKeeper's private CA. To avoid browser warnings (and for"
        echo "  web sessions inside NKeeper), install this CA as trusted on each client machine:"
        echo "    $ca_file"
        echo "  (Windows: certutil -addstore -f Root nkeeper-ca.crt as administrator; macOS: add to"
        echo "  the System keychain and trust it; Linux: copy to /usr/local/share/ca-certificates/ and"
        echo "  run update-ca-certificates.) Or re-run with --cert/--key to use your own certificate."
    fi
    echo
    echo "  Web sessions open on nkw-<id>.${web_domain:-web.$(first_name)}: add a wildcard DNS record"
    echo "  (*.${web_domain:-web.$(first_name)}) pointing at this server to use them from other machines."
    if [ "$mode" = docker ]; then
        echo "  Upgrade: git pull, then run this script again. Logs: docker compose logs -f backend"
    else
        echo "  Upgrade: git pull, then run this script again. Logs: journalctl -u nkeeper -f"
    fi
}

# --- Main ----------------------------------------------------------------------------------------

if $uninstall; then
    if [ -z "$mode" ]; then
        if [ -f /etc/systemd/system/nkeeper.service ]; then mode=native; else mode=docker; fi
    fi
    if [ "$mode" = native ]; then native_uninstall; else docker_uninstall; fi
    exit 0
fi

command -v curl >/dev/null 2>&1 || die "curl is required (install it with your package manager)."
choose_settings
if [ "$mode" = docker ]; then docker_install; else native_install; fi

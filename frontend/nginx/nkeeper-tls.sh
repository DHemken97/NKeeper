#!/bin/sh
# NKeeper's HTTPS for nginx, shared by the frontend container and native Linux installs.
#
#   nkeeper-tls.sh certificate DIR NAME...
#       Makes sure DIR holds a certificate for NAME... (host names, IP addresses, or *.domain
#       wildcards). Your own certificate, DIR/custom.crt + DIR/custom.key, is always used when
#       present. Otherwise NKeeper keeps a private CA (DIR/ca.crt, to install on client machines)
#       and issues DIR/server.crt from it, renewed when the names change or it nears expiry.
#       Every host name also gets *.web.<name>, for web sessions (nkw-<id>.web.<name>).
#
#   nkeeper-tls.sh config TEMPLATE OUTPUT DIR HTTPS_LISTEN REDIRECT_LISTEN PUBLIC_HTTPS_PORT ROOT SERVER_NAMES
#       Writes nginx's HTTPS server config from tls.conf.template, using DIR's certificate.
set -eu

is_ip() {
    case "$1" in
        *:*) return 0 ;;
        *[!0-9.]*) return 1 ;;
        *.*.*.*) return 0 ;;
        *) return 1 ;;
    esac
}

certificate() {
    dir=$1
    shift
    mkdir -p "$dir"
    if [ -s "$dir/custom.crt" ] && [ -s "$dir/custom.key" ]; then
        return 0
    fi

    san=""
    first=""
    for name in localhost 127.0.0.1 "$@"; do
        [ -n "$name" ] || continue
        if is_ip "$name"; then
            entry="IP:$name"
        else
            entry="DNS:$name"
            case "$name" in
                \*.*) ;;
                *) entry="$entry,DNS:*.web.$name" ;;
            esac
            [ -n "$first" ] || [ "$name" = localhost ] || first=$name
        fi
        case ",$san," in
            *",$entry,"*) ;;
            *) san="${san:+$san,}$entry" ;;
        esac
    done
    first=${first:-localhost}

    umask 077
    if [ ! -s "$dir/ca.key" ] || [ ! -s "$dir/ca.crt" ]; then
        openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -sha256 -days 3650 \
            -keyout "$dir/ca.key" -out "$dir/ca.crt" -subj "/O=NKeeper/CN=NKeeper private CA ($first)" \
            -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
            -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null
        rm -f "$dir/server.crt"
    fi

    if [ -s "$dir/server.crt" ] && [ "$(cat "$dir/server.names" 2>/dev/null)" = "$san" ] &&
        openssl x509 -checkend 2592000 -noout -in "$dir/server.crt" >/dev/null 2>&1; then
        return 0
    fi

    printf 'subjectAltName=%s\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=serverAuth\n' "$san" >"$dir/server.ext"
    openssl req -new -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -sha256 \
        -keyout "$dir/server.key" -out "$dir/server.csr" -subj "/O=NKeeper/CN=$first" 2>/dev/null
    # 825 days: the longest browsers accept for certificates from a private CA.
    openssl x509 -req -sha256 -days 825 -in "$dir/server.csr" -CA "$dir/ca.crt" -CAkey "$dir/ca.key" \
        -set_serial "0x$(openssl rand -hex 16)" -extfile "$dir/server.ext" -out "$dir/server.crt" 2>/dev/null
    rm -f "$dir/server.csr" "$dir/server.ext"
    printf '%s' "$san" >"$dir/server.names"
    chmod 644 "$dir/ca.crt" "$dir/server.crt"
    echo "nkeeper-tls: issued a certificate for $san"
}

config() {
    template=$1 output=$2 dir=$3 https_listen=$4 redirect_listen=$5 public_port=$6 root=$7 names=$8
    if [ -s "$dir/custom.crt" ] && [ -s "$dir/custom.key" ]; then
        cert="$dir/custom.crt" key="$dir/custom.key"
    else
        cert="$dir/server.crt" key="$dir/server.key"
    fi
    suffix=""
    [ "$public_port" = 443 ] || suffix=":$public_port"
    sed -e "s|@HTTPS_LISTEN@|$(sed_value "$https_listen")|g" -e "s|@REDIRECT_LISTEN@|$(sed_value "$redirect_listen")|g" \
        -e "s|@HTTPS_PORT_SUFFIX@|$suffix|g" -e "s|@ROOT@|$(sed_value "$root")|g" -e "s|@CERT@|$(sed_value "$cert")|g" \
        -e "s|@KEY@|$(sed_value "$key")|g" -e "s|@SERVER_NAMES@|$(sed_value "$names")|g" "$template" >"$output"
}

# Escapes a value for the replacement side of sed's s|...|...|.
sed_value() { printf '%s' "$1" | sed -e 's/[\\|&]/\\&/g'; }

command=${1:-}
[ $# -gt 0 ] && shift
case "$command" in
    certificate) certificate "$@" ;;
    config) config "$@" ;;
    *) echo "usage: nkeeper-tls.sh certificate DIR NAME... | config TEMPLATE OUTPUT DIR HTTPS_LISTEN REDIRECT_LISTEN PUBLIC_HTTPS_PORT ROOT SERVER_NAMES" >&2; exit 2 ;;
esac

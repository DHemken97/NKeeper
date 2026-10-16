#!/bin/sh
# Container start-up (run by nginx's /docker-entrypoint.sh): make sure the HTTPS certificate exists
# for NKEEPER_SERVER_NAMES and write conf.d/tls.conf. The certificate lives in the "tls" volume.
set -eu

# Space-separated names and addresses clients use to open NKeeper.
names=${NKEEPER_SERVER_NAMES:-localhost}
web_domain=${NKEEPER_WEB_BASE_DOMAIN:-}

# shellcheck disable=SC2086 # names are split on purpose
/usr/local/bin/nkeeper-tls.sh certificate /etc/nginx/tls $names ${web_domain:+"*.$web_domain"}
/usr/local/bin/nkeeper-tls.sh config /etc/nginx/nkeeper/tls.conf.template /etc/nginx/conf.d/tls.conf \
    /etc/nginx/tls 8443 8081 "${NKEEPER_HTTPS_PORT:-8443}" /usr/share/nginx/html _

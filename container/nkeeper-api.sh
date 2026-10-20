#!/bin/sh
# Starts the API (run by supervisord as the nkeeper user) once PostgreSQL accepts connections.
# Its settings are in /run/nkeeper/api.env, written by the entrypoint and readable only by nkeeper,
# so the other services never see them.
set -eu

tries=0
until /usr/lib/postgresql/17/bin/pg_isready -q -h /run/postgresql; do
    tries=$((tries + 1))
    [ "$tries" -lt 60 ] || { echo "nkeeper-api: PostgreSQL isn't ready after 60 s." >&2; exit 1; }
    sleep 1
done

set -a
. /run/nkeeper/api.env
set +a
exec /opt/nkeeper/api/NKeeper.Api

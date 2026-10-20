# syntax=docker/dockerfile:1
#
# NKeeper in a single image: nginx (HTTPS), the API, PostgreSQL 17 and guacd 1.6 (RDP/VNC), run
# by supervisord. For hosts that run one container per app, such as a Proxmox VE OCI container.
# The multi-container Compose setup (docker-compose.yml) remains the default install.
#
#   scripts/build-image.sh             (or scripts\build-image.ps1 on Windows)
#
# Built from the repository root: docker buildx build -f container/Dockerfile .
# Everything that must survive an upgrade is under /var/lib/nkeeper; see docs/installation.md.

ARG DEBIAN=trixie-slim

# --- Frontend: the built SPA (platform-independent, so built natively) -------------------------
FROM --platform=$BUILDPLATFORM node:24-alpine AS frontend
WORKDIR /app
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY frontend/ .
# Empty: the browser calls /api on the same origin, which nginx proxies to the API.
ENV VITE_API_BASE_URL=
RUN npm run build

# --- API: published self-contained (there's no Debian-based .NET 10 runtime image) -------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS api
ARG TARGETARCH
WORKDIR /src
COPY backend/Directory.Build.props backend/NKeeper.slnx ./
COPY backend/src/NKeeper.Api/NKeeper.Api.csproj src/NKeeper.Api/
COPY backend/src/NKeeper.Infrastructure/NKeeper.Infrastructure.csproj src/NKeeper.Infrastructure/
RUN rid=linux-$([ "$TARGETARCH" = arm64 ] && echo arm64 || echo x64) \
    && dotnet restore src/NKeeper.Api/NKeeper.Api.csproj -r "$rid"
COPY backend/src/ src/
RUN rid=linux-$([ "$TARGETARCH" = arm64 ] && echo arm64 || echo x64) \
    && dotnet publish src/NKeeper.Api/NKeeper.Api.csproj -c Release -r "$rid" --self-contained -o /out --no-restore

# --- guacd 1.6.0, RDP and VNC only, from source (Debian ships an older guacd) -------------------
FROM debian:${DEBIAN} AS guacd
ARG GUACAMOLE_VERSION=1.6.0
ARG GUACAMOLE_SHA256=8bc45675da96d7b6f39728160181e3d4ff3c08f460f6d26de5805b642bf13f2b
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        build-essential ca-certificates curl pkg-config libtool-bin \
        libcairo2-dev libjpeg62-turbo-dev libpng-dev libwebp-dev uuid-dev libssl-dev \
        freerdp3-dev libvncserver-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /build
RUN curl -fsSL -o guacamole-server.tar.gz \
        "https://archive.apache.org/dist/guacamole/${GUACAMOLE_VERSION}/source/guacamole-server-${GUACAMOLE_VERSION}.tar.gz" \
    && echo "${GUACAMOLE_SHA256}  guacamole-server.tar.gz" | sha256sum -c - \
    && tar -xzf guacamole-server.tar.gz --strip-components=1 \
    # guacamole builds with -Werror; Debian's FreeRDP 3.15 deprecates calls it still makes (they work).
    && ./configure --prefix=/opt/guacamole --disable-guacenc --disable-guaclog --disable-static \
        --without-ssh --without-telnet --without-kubernetes --without-pango \
        CFLAGS="-O2 -Wno-error=deprecated-declarations" \
    && make -j"$(nproc)" \
    && make install \
    # guacd's FreeRDP plugins are installed into FreeRDP's own directory; carry them along.
    && mkdir -p /stage \
    && cp -a --parents /opt/guacamole /stage/ \
    && cp -a --parents /usr/lib/*/freerdp3 /stage/

# --- The image -----------------------------------------------------------------------------------
FROM debian:${DEBIAN}
ENV LANG=C.UTF-8 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# Stop the PostgreSQL package from creating a cluster: the entrypoint creates one in /var/lib/nkeeper.
RUN mkdir -p /etc/postgresql-common \
    && echo 'create_main_cluster = false' >/etc/postgresql-common/createcluster.conf \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates iproute2 openssl supervisor nginx postgresql-17 \
        libicu76 libgssapi-krb5-2 libldap2 python3 python3-venv \
        libcairo2 libjpeg62-turbo libpng16-16t64 libwebp7 libuuid1 \
        libfreerdp3-3 libfreerdp-client3-3 libwinpr3-3 libvncclient1 \
    # Impacket reads Windows hosts over DCOM/WMI for the RPC internal scan.
    && python3 -m venv /opt/impacket \
    && /opt/impacket/bin/pip install --no-cache-dir impacket==0.12.0 \
    && rm -rf /var/lib/apt/lists/* /etc/nginx/sites-enabled/default \
    # Each service runs as its own unprivileged user.
    && useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin nkeeper \
    && useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin guacd

COPY --from=guacd /stage/ /
RUN echo /opt/guacamole/lib >/etc/ld.so.conf.d/guacamole.conf && ldconfig

COPY --from=api /out/ /opt/nkeeper/api/
COPY --from=frontend /app/dist/ /opt/nkeeper/www/
COPY frontend/nginx/snippets/ /etc/nginx/snippets/nkeeper/
COPY frontend/nginx/tls.conf.template /etc/nkeeper/tls.conf.template
COPY --chmod=755 frontend/nginx/nkeeper-tls.sh /usr/local/bin/nkeeper-tls.sh
COPY container/nginx-nkeeper.conf /etc/nginx/conf.d/nkeeper.conf
COPY container/supervisord.conf /etc/supervisor/supervisord.conf
COPY container/nkeeper.env.example /etc/nkeeper/nkeeper.env.example
COPY --chmod=755 container/entrypoint.sh /usr/local/bin/nkeeper-entrypoint
COPY --chmod=755 container/nkeeper-api.sh /usr/local/bin/nkeeper-api

ARG NKEEPER_VERSION=dev
LABEL org.opencontainers.image.title="NKeeper" \
      org.opencontainers.image.description="Web-based remote connection manager: nginx, API, PostgreSQL and guacd in one image" \
      org.opencontainers.image.version="${NKEEPER_VERSION}"

VOLUME /var/lib/nkeeper
# HTTPS, and plain HTTP that redirects to it.
EXPOSE 443 80
STOPSIGNAL SIGTERM
HEALTHCHECK --interval=30s --timeout=5s --start-period=120s \
    CMD python3 -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:5080/health/ready', timeout=4).status == 200 else 1)"
ENTRYPOINT ["/usr/local/bin/nkeeper-entrypoint"]

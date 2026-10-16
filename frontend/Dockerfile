# syntax=docker/dockerfile:1

FROM node:24-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY . .
# Empty (default) = call the API on the same origin; nginx below proxies /api to the backend.
ARG VITE_API_BASE_URL=
ENV VITE_API_BASE_URL=${VITE_API_BASE_URL}
RUN npm run build

# Unprivileged nginx runs as a non-root user: HTTP on 8080, HTTPS on 8443, HTTP->HTTPS redirect on 8081.
FROM nginxinc/nginx-unprivileged:1.28-alpine AS runtime
USER root
# openssl issues the self-signed certificate at start-up; the "tls" volume keeps it (owned by nginx).
RUN apk add --no-cache openssl     && mkdir -p /etc/nginx/tls /etc/nginx/nkeeper     && chown -R nginx:nginx /etc/nginx/tls /etc/nginx/conf.d
COPY nginx/default.conf /etc/nginx/conf.d/default.conf
COPY nginx/snippets/ /etc/nginx/snippets/nkeeper/
COPY nginx/tls.conf.template /etc/nginx/nkeeper/tls.conf.template
COPY --chmod=755 nginx/nkeeper-tls.sh /usr/local/bin/nkeeper-tls.sh
COPY --chmod=755 nginx/40-nkeeper-tls.sh /docker-entrypoint.d/40-nkeeper-tls.sh
COPY --from=build /app/dist /usr/share/nginx/html
USER 101
VOLUME /etc/nginx/tls
EXPOSE 8080 8443 8081

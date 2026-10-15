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

# Unprivileged nginx listens on 8080 and runs as a non-root user.
FROM nginxinc/nginx-unprivileged:1.28-alpine AS runtime
COPY nginx/default.conf /etc/nginx/conf.d/default.conf
COPY nginx/security-headers.conf /etc/nginx/snippets/security-headers.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080

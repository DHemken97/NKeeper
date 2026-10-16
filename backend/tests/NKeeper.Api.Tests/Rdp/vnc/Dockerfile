# A minimal VNC server for integration tests: x11vnc on a virtual X display, password protected.
FROM alpine:3.22
RUN apk add --no-cache x11vnc xvfb xterm
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
ENTRYPOINT ["/entrypoint.sh"]

#!/bin/sh
# Stands in for journalctl: records its arguments, and answers from /var/lib/fake-linux/journal, with
# entries in the format of real journalctl --output=json (systemd 252). Needs root, like reading the
# whole journal does, unless /var/lib/fake-linux/journal-open exists.
d=/var/lib/fake-linux
printf '%s\n' "$*" >> "$d/journalctl-args"
if [ "$(id -u)" != 0 ] && [ ! -e "$d/journal-open" ]; then
  echo "No journal files were opened due to insufficient permissions." >&2
  exit 1
fi
if [ -e "$d/journal-open" ] && [ "$(id -u)" != 0 ]; then
  echo "Hint: You are currently not seeing messages from other users and the system." >&2
fi
follow=; grep=
for a in "$@"; do
  case "$a" in
    --follow) follow=1 ;;
    --grep=*) grep=${a#--grep=} ;;
  esac
done
case "$grep" in
  '(') echo 'Bad pattern "(": missing closing parenthesis' >&2; exit 1 ;;
esac
if [ -n "$follow" ]; then
  cat "$d/journal-new"
  # Like the real one, it stays until stopped.
  exec sleep 300
fi
if [ -n "$grep" ] && ! grep -q "$grep" "$d/journal"; then
  exit 1 # What real journalctl does when --grep matches nothing: exit 1, saying nothing.
fi
cat "$d/journal"

#!/bin/sh
# Stands in for checkmodule -M -m -o out.mod in.te: refuses what doesn't end in a semicolon, as a
# stand-in for a syntax error, and otherwise "compiles" by copying.
case "$(tail -n 1 "$5")" in
  *\;|*\}) cp "$5" "$4" ;;
  *) echo "$5:3:ERROR 'syntax error' at token '0' on line 3:" >&2; echo "checkmodule:  error(s) encountered while parsing configuration" >&2; exit 1 ;;
esac

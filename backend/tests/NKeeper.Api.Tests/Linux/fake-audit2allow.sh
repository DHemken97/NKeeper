#!/bin/sh
# Stands in for audit2allow -m name: a module with one rule for each AVC on its stdin, formatted as the
# real one does.
name=$2
in=$(cat)
if [ -z "$in" ]; then exit 0; fi
printf '\nmodule %s 1.0;\n\nrequire {\n' "$name"
printf '\ttype httpd_t;\n\ttype user_home_t;\n\tclass file read;\n}\n\n#============= httpd_t ==============\n\n'
printf "#!!!! This avc can be allowed using the boolean 'httpd_read_user_content'\n"
printf 'allow httpd_t user_home_t:file read;\n'

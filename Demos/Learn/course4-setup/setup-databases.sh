#!/usr/bin/env bash
# Create the Course 4 cookbook databases on each sandbox engine, or on your own server's
# single activated engine (LEARN_SERVER). Re-running is safe -- all DDL is idempotent.
# PASS is reported only after the database is confirmed to exist (a create that silently
# fails reports FAIL, never a false PASS).
#
# --reset drops and recreates them empty. Only databases the labs created are ever dropped --
# see lab_remove_db.
set -u

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/../lab-sql.sh"

reset=0
[ "${1:-}" = "--reset" ] && reset=1

fail=0
total=0

label() {
  case "$1" in
    sqlserver) printf 'SQL Server' ;;
    postgres)  printf 'PostgreSQL' ;;
    mysql)     printf 'MySQL' ;;
    mariadb)   printf 'MariaDB' ;;
  esac
}

databases=(
  cookbook_r1_prod
  cookbook_r1_nonprod
  cookbook_r2
  cookbook_r3
  cookbook_r4
  cookbook_r5
  cookbook_r6
  cookbook_r8
  cookbook_r9
)

engines="$(lab_engines)" || exit 1
for engine in $engines; do
  echo "$(label "$engine")"
  for db in "${databases[@]}"; do
    printf '  %-26s ' "$db"
    total=$((total + 1))
    rc=0
    err=''
    if [ "$reset" -eq 1 ]; then
      removed="$(lab_remove_db "$engine" "$db" 2>/dev/null)"
      if [ "$removed" = "refused" ]; then
        err="'$db' exists but wasn't created by the labs, so it will not be dropped. Rename or move it, then re-run."
        rc=1
      fi
    fi
    if [ "$rc" -eq 0 ]; then
      err="$(lab_confirm_db "$engine" "$db" 2>&1 1>/dev/null)"
      rc=$?
    fi
    if [ "$rc" -eq 0 ]; then
      if [ "$reset" -eq 1 ]; then echo "PASS (reset)"; else echo "PASS"; fi
    else
      echo "FAIL"
      echo "$err" | sed 's/^/    /'
      fail=1
    fi
  done
done

echo
if [ "$fail" -eq 0 ]; then
  echo "All ${total} databases are ready."
  exit 0
else
  echo "One or more databases could not be created. Is the sandbox up (or your own server reachable)? See Demos/Learn/README.md."
  exit 1
fi

#!/usr/bin/env bash
# Create the Course 8 Module 7 log-hygiene sandbox database on each sandbox
# engine, or on your own server's single activated engine (LEARN_SERVER): ONE EMPTY
# database diag_hygiene. No schema is seeded -- the module deploys a small settings
# package into it. Re-running is safe -- creation is idempotent. PASS is reported only
# after the database is confirmed to exist (a create that silently fails reports FAIL,
# never a false PASS).
#
# --reset drops and recreates it empty. Use it to start the module's walkthrough over from
# a clean slate after it has broken the database on purpose. Only a database the labs
# created is ever dropped -- see lab_remove_db.
set -u

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/../lab-sql.sh"

db="diag_hygiene"
reset=0
[ "${1:-}" = "--reset" ] && reset=1

label() {
  case "$1" in
    sqlserver) printf 'SQL Server' ;;
    postgres)  printf 'PostgreSQL' ;;
    mysql)     printf 'MySQL' ;;
    mariadb)   printf 'MariaDB' ;;
  esac
}

fail=0
total=0

engines="$(lab_engines)" || exit 1
for engine in $engines; do
  printf '%-12s ' "$(label "$engine")"
  total=$((total + 1))
  rc=0
  err=''
  if [ "$reset" -eq 1 ]; then
    removed="$(lab_remove_db "$engine" "$db" 2>/dev/null)"
    if [ "$removed" = "refused" ]; then
      err="    '$db' exists but wasn't created by the labs, so it will not be dropped. Rename or move it, then re-run."
      rc=1
    fi
  fi
  if [ "$rc" -eq 0 ]; then
    err="$(lab_confirm_db "$engine" "$db" 2>&1 1>/dev/null)"
    rc=$?
  fi
  if [ "$rc" -eq 0 ]; then
    if [ "$reset" -eq 1 ]; then echo "PASS (reset)"; else echo "PASS (log-hygiene sandbox database ready)"; fi
  else
    echo "FAIL"
    echo "$err" | sed 's/^/    /'
    fail=1
  fi
done

echo
if [ "$fail" -eq 0 ]; then
  where="all ${total} engines"
  [ "$total" -eq 1 ] && where="$(label "$engines")"
  echo "log-hygiene sandbox database ready on ${where} — empty, ready for schema deploy."
  exit 0
else
  echo "One or more engines could not be set up. Is the sandbox up (or your own server reachable)? See Demos/Learn/README.md."
  exit 1
fi

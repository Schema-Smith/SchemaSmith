#!/usr/bin/env bash
# Create + seed the Course 5 migration-track databases on each sandbox engine, or on your
# own server's single activated engine (LEARN_SERVER). Each database is the post-migration
# state a source tool would have left behind: the shared shop schema plus that tool's own
# bookkeeping table; Module 6 adds a dev/prod pair, the second carrying production-only
# drift. Re-running is safe -- all DDL is idempotent. PASS is reported only
# after a shop table is confirmed to exist (a seed that silently fails reports FAIL, never
# a false PASS).
#
# --reset drops and recreates the databases empty before reseeding. Only databases the labs
# created are ever dropped -- see lab_remove_db.
set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$SCRIPT_DIR/../lab-sql.sh"

reset=0
[ "${1:-}" = "--reset" ] && reset=1

fail=0

label() {
  case "$1" in
    sqlserver) printf 'SQL Server' ;;
    postgres)  printf 'PostgreSQL' ;;
    mysql)     printf 'MySQL' ;;
    mariadb)   printf 'MariaDB' ;;
  esac
}

# create/reset + seed shop + optional tracker + confirm a shop table exists.
init_db() {
  local engine="$1" db="$2" tracker="$3" err out ready_sql removed

  printf '  %-26s ' "$db"

  if [ "$reset" -eq 1 ]; then
    removed="$(lab_remove_db "$engine" "$db" 2>/dev/null)"
    if [ "$removed" = "refused" ]; then
      echo "FAIL"
      echo "    '$db' exists but wasn't created by the labs, so it will not be dropped. Rename or move it, then re-run."
      fail=1
      return 1
    fi
  fi

  err="$(lab_confirm_db "$engine" "$db" 2>&1 1>/dev/null)"
  if [ $? -ne 0 ]; then echo "FAIL"; echo "$err" | sed 's/^/    /'; fail=1; return 1; fi

  err="$(lab_sql_file "$engine" "$db" "$SCRIPT_DIR/seed/$engine/shop.sql" 2>&1)"
  if [ $? -ne 0 ]; then echo "FAIL"; echo "$err" | sed 's/^/    /'; fail=1; return 1; fi

  if [ -n "$tracker" ]; then
    err="$(lab_sql_file "$engine" "$db" "$SCRIPT_DIR/seed/$engine/$tracker.sql" 2>&1)"
    if [ $? -ne 0 ]; then echo "FAIL"; echo "$err" | sed 's/^/    /'; fail=1; return 1; fi
  fi

  case "$engine" in
    sqlserver) ready_sql="SELECT 'READY' WHERE OBJECT_ID('dbo.Customer') IS NOT NULL" ;;
    postgres)  ready_sql="SELECT 1 FROM information_schema.tables WHERE table_name='customer'" ;;
    *)         ready_sql="SELECT 1 FROM information_schema.tables WHERE table_schema='$db' AND table_name='Customer'" ;;
  esac
  out="$(lab_sql "$engine" "$db" "$ready_sql" 2>&1)"
  if [ $? -ne 0 ]; then echo "FAIL"; echo "$out" | sed 's/^/    /'; fail=1; return 1; fi
  if ! echo "$out" | grep -qE 'READY|1'; then
    echo "FAIL"
    echo "    seed completed but the shop schema's Customer table was not found."
    fail=1
    return 1
  fi

  if [ "$reset" -eq 1 ]; then echo "PASS (reset)"; else echo "PASS"; fi
}

# database : extra seed file  (a source tool's bookkeeping table, or Module 6's production drift; empty = none)
three_engine_dbs=(
  "shop_from_flyway:tracker_flyway"
  "shop_from_liquibase:tracker_liquibase"
  "shop_from_efcore:tracker_efcore"
  "shop_from_scripts:tracker_scripts"
  "shop_brownfield_dev:"
  "shop_brownfield_prod:prod_drift"
)

engines="$(lab_engines)" || exit 1
total=0
parts=""
for engine in $engines; do
  echo "$(label "$engine")"
  n=0
  for pair in "${three_engine_dbs[@]}"; do
    if init_db "$engine" "${pair%%:*}" "${pair##*:}"; then n=$((n + 1)); fi
  done
  # DACPAC is a SQL Server technology (Course 5 Module 4 is SQL-Server-only by design); no tracker table.
  if [ "$engine" = "sqlserver" ]; then
    if init_db "$engine" shop_from_dacpac ""; then n=$((n + 1)); fi
  fi
  total=$((total + n))
  parts="${parts}${parts:+, }${n} $(label "$engine")"
done

echo
if [ "$fail" -eq 0 ]; then
  echo "All ${total} databases are seeded and ready (${parts})."
  exit 0
else
  echo "One or more databases could not be seeded. Is the sandbox up (or your own server reachable)? See Demos/Learn/README.md."
  exit 1
fi

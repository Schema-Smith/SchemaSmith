#!/usr/bin/env bash
# Create the Course 11 "Vault" databases on each sandbox engine, or on your own server's single
# activated engine (LEARN_SERVER): vault_m1 .. vault_m4, one per module, all empty.
#
# It also creates the SQL Server partition function and scheme Module 1 needs -- deliberately.
# SchemaSmith names a partition scheme and NEVER creates one, exactly as it never creates a
# filegroup: those are server-side objects a DBA owns. That asymmetry is Module 1's first lesson,
# so this script standing them up for you is part of the teaching, not a convenience.
#
# Re-running is safe -- every create is guarded. PASS is reported only after the object is
# confirmed to exist. --reset drops and recreates the databases empty; only a database the labs
# created is ever dropped (see lab_remove_db).
set -u

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/../lab-sql.sh"

dbs="vault_m1 vault_m2 vault_m3 vault_m4"
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

# The partition function + scheme Module 1 declares by NAME. SQL Server only -- PostgreSQL and
# MySQL/MariaDB express partitioning on the table itself, so they need nothing here.
seed_sqlserver_partitioning() {
  db="$1"
  lab_sql sqlserver "$db" "
IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = 'pf_vault_year')
  CREATE PARTITION FUNCTION pf_vault_year (INT) AS RANGE RIGHT FOR VALUES (2024, 2025, 2026);
IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = 'ps_vault_year')
  CREATE PARTITION SCHEME ps_vault_year AS PARTITION pf_vault_year ALL TO ([PRIMARY]);
-- A SECOND scheme over the same function. Module 1 declares a move from one to the other, which is
-- the change SchemaSmith refuses -- so the lab needs somewhere real to try to move to.
IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = 'ps_vault_year_alt')
  CREATE PARTITION SCHEME ps_vault_year_alt AS PARTITION pf_vault_year ALL TO ([PRIMARY]);
" >/dev/null 2>&1
  # Confirm rather than assume -- a create that silently failed must not report PASS.
  n="$(lab_sql sqlserver "$db" "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.partition_schemes WHERE name IN ('ps_vault_year','ps_vault_year_alt')" 2>/dev/null | tr -dc '0-9')"
  [ "$n" = "2" ]
}

# The PostgreSQL tablespace Module 1 declares by NAME. PostgreSQL will not create the server-side
# directory itself ("directory ... does not exist"), and a lab cannot assume one is there -- so we make
# the server create it over the same connection. COPY ... TO PROGRAM is superuser-only, which costs
# nothing here because CREATE TABLESPACE is too. Same technique the product's own tablespace tests use.
seed_postgres_tablespace() {
  # CREATE TABLESPACE has no IF NOT EXISTS, so check first rather than relying on the error being
  # discarded -- a swallowed failure and an already-present tablespace must not look the same.
  have="$(lab_sql postgres postgres "SELECT COUNT(*) FROM pg_tablespace WHERE spcname = 'vault_ts'" 2>/dev/null | tr -dc '0-9')"
  if [ "$have" != "1" ]; then
    lab_sql postgres postgres "COPY (SELECT 1) TO PROGRAM 'mkdir -p /var/lib/postgresql/vault_ts'" >/dev/null 2>&1
    lab_sql postgres postgres "CREATE TABLESPACE vault_ts LOCATION '/var/lib/postgresql/vault_ts'" >/dev/null 2>&1
  fi
  # Confirm rather than assume -- a tablespace that silently failed to create must not report PASS.
  n="$(lab_sql postgres postgres "SELECT COUNT(*) FROM pg_tablespace WHERE spcname = 'vault_ts'" 2>/dev/null | tr -dc '0-9')"
  [ "$n" = "1" ]
}

engines="$(lab_engines)" || exit 1
for engine in $engines; do
  printf '%-12s\n' "$(label "$engine")"
  for db in $dbs; do
    printf '  %-24s ' "$db"
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
    if [ "$rc" -eq 0 ] && [ "$engine" = "sqlserver" ] && [ "$db" = "vault_m1" ]; then
      if ! seed_sqlserver_partitioning "$db"; then
        err="database created, but the partition function/scheme could not be confirmed in $db."
        rc=1
      fi
    fi
    if [ "$rc" -eq 0 ] && [ "$engine" = "postgres" ] && [ "$db" = "vault_m1" ]; then
      if ! seed_postgres_tablespace; then
        err="database created, but the vault_ts tablespace could not be confirmed."
        rc=1
      fi
    fi
    if [ "$rc" -eq 0 ]; then
      if [ "$engine" = "sqlserver" ] && [ "$db" = "vault_m1" ]; then
        echo "PASS (+ pf_vault_year / ps_vault_year / ps_vault_year_alt)"
      elif [ "$engine" = "postgres" ] && [ "$db" = "vault_m1" ]; then
        echo "PASS (+ vault_ts tablespace)"
      else
        echo "PASS"
      fi
    else
      echo "FAIL"
      echo "$err" | sed 's/^/      /'
      fail=1
    fi
  done
done

echo
if [ "$fail" -eq 0 ]; then
  echo "Vault databases ready — empty, ready for Module 1."
  exit 0
else
  echo "One or more engines could not be set up. Is the sandbox up (or your own server reachable)? See Demos/Learn/README.md."
  exit 1
fi

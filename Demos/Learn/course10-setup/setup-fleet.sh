#!/usr/bin/env bash
# Course 10 -- stand up the mixed-version fleet the rest of the course deploys into.
#
# One package, many engine versions, all live at once. This script:
#   (a) brings up the opt-in `mixed-fleet` compose profile (floor-version PostgreSQL 12,
#       MySQL 5.7, MariaDB 10.2 running ALONGSIDE the current-tier engines);
#   (b) waits for each floor engine (and SQL Server) to report healthy;
#   (c) provisions three SQL Server tier databases on the ONE learn-sqlserver instance at
#       differing compatibility_level -- learn_2022 (160), learn_2016 (130), learn_2008 (100)
#       -- because compat level, not the binary, is what actually gates T-SQL syntax;
#   (d) confirms the `learn` database exists on each floor engine (the images auto-create it
#       from env vars; this verifies and back-fills if one somehow did not).
#
# Re-running is safe. Every step is idempotent: databases are created only if absent, and the
# ALTER ... SET COMPATIBILITY_LEVEL is a no-op when the level already matches. PASS is reported
# per tier/engine only after the target is confirmed at the intended state.
#
# Course 10's mixed fleet is SANDBOX-ONLY: a single own-server endpoint cannot be several engine
# versions at once, so there is no own-server path here (unlike the other courses' setup labs).
# This script reaches the containers directly with `docker exec`; it does not source lab-sql.sh.
set -u

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOCKER_DIR="$HERE/../docker"

SQLSERVER_CONTAINER='learn-sqlserver'
PG_FLOOR_CONTAINER='learn-postgres-12'
MYSQL_FLOOR_CONTAINER='learn-mysql-57'
MARIA_FLOOR_CONTAINER='learn-mariadb-102'

fail=0

# --- engine access (direct docker exec -- self-contained, no lab-sql.sh) ---------------------

# MSYS_NO_PATHCONV keeps Git Bash from rewriting the container's /opt/... path; harmless elsewhere.
ss_exec() {
  # 2017 ships only /opt/mssql-tools; 2019+ ship the 18 path. This script is deliberately
  # self-contained (no lab-sql.sh), so it resolves the binary itself rather than importing one.
  sc=/opt/mssql-tools18/bin/sqlcmd
  docker exec "$SQLSERVER_CONTAINER" sh -c "test -x $sc" >/dev/null 2>&1 || sc=/opt/mssql-tools/bin/sqlcmd
  MSYS_NO_PATHCONV=1 docker exec "$SQLSERVER_CONTAINER" "$sc" \
    -S localhost -U sa -P 'Learn!Passw0rd' -C -b -h -1 -W -Q "SET NOCOUNT ON; $1" 2>&1
}

pg_floor_exec() {
  docker exec "$PG_FLOOR_CONTAINER" psql -U postgres -d postgres -v ON_ERROR_STOP=1 -tAc "$1" 2>&1
}

# MySQL 5.7 and MariaDB 10.2 both ship only the `mysql` client and both take the MYSQL_* env vars.
mysql_floor_exec() {
  docker exec -e 'MYSQL_PWD=Learn!Passw0rd' "$1" mysql -uroot -N -s -e "$2" 2>&1
}

# --- health wait -----------------------------------------------------------------------------

wait_healthy() {
  local container="$1" label="$2" i status
  printf '%-34s ' "waiting: $label"
  for i in $(seq 1 180); do
    status="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container" 2>/dev/null || true)"
    if [ "$status" = "healthy" ]; then echo "healthy"; return 0; fi
    if [ $((i % 6)) -eq 0 ]; then printf '.'; fi
    sleep 5
  done
  echo "TIMED OUT (last status: ${status:-unknown})"
  return 1
}

# --- provisioning ----------------------------------------------------------------------------

trim() { printf '%s' "$1" | tr -d '[:space:]'; }

report_pass() { printf '%-34s PASS (%s)\n' "$1" "$2"; }
report_fail() { printf '%-34s FAIL\n' "$1"; [ -n "${2:-}" ] && printf '%s\n' "$2" | sed 's/^/    /'; fail=1; }

provision_sqlserver_tier() {
  local label="$1" db="$2" compat="$3" out got
  out="$(ss_exec "IF DB_ID('$db') IS NULL CREATE DATABASE [$db]; ALTER DATABASE [$db] SET COMPATIBILITY_LEVEL = $compat;")"
  if [ $? -ne 0 ]; then report_fail "$label" "$out"; return; fi
  got="$(trim "$(ss_exec "SELECT compatibility_level FROM sys.databases WHERE name = '$db'")")"
  if [ "$got" = "$compat" ]; then
    report_pass "$label" "compat $compat"
  else
    report_fail "$label" "expected compatibility_level $compat, got '${got:-none}'"
  fi
}

confirm_pg_floor() {
  local label="$1" out exists
  exists="$(trim "$(pg_floor_exec "SELECT COUNT(*) FROM pg_database WHERE datname = 'learn'")")"
  if [ "$exists" != "1" ]; then
    out="$(pg_floor_exec "CREATE DATABASE learn")"
    if [ $? -ne 0 ]; then report_fail "$label" "$out"; return; fi
    exists="$(trim "$(pg_floor_exec "SELECT COUNT(*) FROM pg_database WHERE datname = 'learn'")")"
  fi
  if [ "$exists" = "1" ]; then report_pass "$label" "learn ready"; else report_fail "$label" "learn database missing"; fi
}

confirm_mysql_floor() {
  local label="$1" container="$2" out exists
  out="$(mysql_floor_exec "$container" "CREATE DATABASE IF NOT EXISTS \`learn\`")"
  if [ $? -ne 0 ]; then report_fail "$label" "$out"; return; fi
  exists="$(trim "$(mysql_floor_exec "$container" "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = 'learn'")")"
  if [ "$exists" = "1" ]; then report_pass "$label" "learn ready"; else report_fail "$label" "learn database missing"; fi
}

# --- run -------------------------------------------------------------------------------------

echo "Bringing up the mixed-version fleet (docker compose --profile mixed-fleet up -d)..."
if ! ( cd "$DOCKER_DIR" && docker compose --profile mixed-fleet up -d ); then
  echo "FAIL: could not start the mixed-fleet profile. Is Docker running? See Demos/Learn/README.md." >&2
  exit 1
fi
echo

echo "Waiting for engines to report healthy (floor images boot in seconds; a cold SQL Server can take longer)..."
wait_healthy "$SQLSERVER_CONTAINER"   "SQL Server (learn-sqlserver)"   || fail=1
wait_healthy "$PG_FLOOR_CONTAINER"    "PostgreSQL 12 (floor)"          || fail=1
wait_healthy "$MYSQL_FLOOR_CONTAINER" "MySQL 5.7 (floor)"              || fail=1
wait_healthy "$MARIA_FLOOR_CONTAINER" "MariaDB 10.2 (floor)"           || fail=1
echo

if [ "$fail" -ne 0 ]; then
  echo "One or more engines never became healthy -- cannot provision. Re-run once Docker settles, or see Demos/Learn/README.md." >&2
  exit 1
fi

echo "Provisioning SQL Server compatibility tiers on learn-sqlserver (11433)..."
provision_sqlserver_tier "SQL Server tier learn_2022"  "learn_2022" 160
provision_sqlserver_tier "SQL Server tier learn_2016"  "learn_2016" 130
provision_sqlserver_tier "SQL Server tier learn_2008"  "learn_2008" 100
echo

echo "Confirming the learn database on each floor engine..."
confirm_pg_floor    "PostgreSQL 12 floor (15433)"
confirm_mysql_floor "MySQL 5.7 floor (13316)"     "$MYSQL_FLOOR_CONTAINER"
confirm_mysql_floor "MariaDB 10.2 floor (13317)"  "$MARIA_FLOOR_CONTAINER"
echo

if [ "$fail" -ne 0 ]; then
  echo "One or more targets could not be provisioned. See the FAIL detail above." >&2
  exit 1
fi

cat <<'EOF'
The mixed-version fleet is ready. One package will deploy across every tier below.

  Engine       Current tier              Floor tier (--profile mixed-fleet)
  ----------   -----------------------   ----------------------------------
  SQL Server   localhost,11433           (same instance, per-database compatibility_level)
                 learn_2022  compat 160
                 learn_2016  compat 130
                 learn_2008  compat 100
  PostgreSQL   localhost:15432  (16)     localhost:15433  (12)
  MySQL        localhost:13306  (8.0)    localhost:13316  (5.7)
  MariaDB      localhost:13307  (11.4)   localhost:13317  (10.2)

  All engines: user as in Demos/Learn/README.md, password Learn!Passw0rd, database learn
  (SQL Server tiers use learn_2022 / learn_2016 / learn_2008 instead of learn).

Next: Module 1 reads the detected version of every target with the pre-flight pair.
EOF
exit 0

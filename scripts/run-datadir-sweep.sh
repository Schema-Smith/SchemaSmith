#!/usr/bin/env bash
# Paths below are repo-relative; this anchors the script so it runs from anywhere.
cd "$(dirname "$0")/.." || exit 1
# DATA DIRECTORY sweep (LOCAL-ONLY). InnoDB DATA DIRECTORY placement (F2c) needs a pre-created, mysql-owned
# filesystem directory the stock demo/CI containers do not have (and MySQL additionally needs that
# directory listed in --innodb_directories) -- tests cannot docker exec to create one, so the DataDirectory
# category runs here, against purpose-built images, the same way the Encryption sweep and the genuine-
# binary sweep run outside the normal gate. See scripts/test-infra/datadir/.
#
# Unlike the Encryption sweep, BOTH engines are covered here -- MySQL's directory allow-list is genuine
# server configuration (the image bakes it in), not a blocked keyring plugin.
set -u

TEST_USER='TestUser'
TEST_PASSWORD='aCa2d805-41E5@40c4!98e7#92F93zzxo176'
PROJ='Schema/Schema.IntegrationTests/Schema.IntegrationTests.csproj'
KEEP=${KEEP_CONTAINERS:-0}
RECORD='docs/development/datadir-sweep-results.md'
SWEEP_SHA="$(git rev-parse --short HEAD)"
SWEEP_STARTED="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
SWEEP_ROWS=""
FAILED=0

MARIADB_NAME='datadir-mariadb-114'
MARIADB_PORT=13418
MYSQL_NAME='datadir-mysql-80'
MYSQL_PORT=13480

cleanup() {
  [ "$KEEP" = "1" ] && { echo "KEEP_CONTAINERS=1 -- leaving the datadir containers up"; return; }
  docker rm -f "$MARIADB_NAME" "$MYSQL_NAME" >/dev/null 2>&1
}
trap cleanup EXIT

echo "===== DataDirectory sweep (MariaDB + MySQL, local-only) ====="
echo "Building the DataDirectory images..."
if ! docker build -q -t schemasmith-datadir-mariadb:11.4 scripts/test-infra/datadir/mariadb >/dev/null 2>&1; then
  echo "FAIL: could not build the MariaDB datadir image."; exit 1
fi
if ! docker build -q -t schemasmith-datadir-mysql:8.0 scripts/test-infra/datadir/mysql >/dev/null 2>&1; then
  echo "FAIL: could not build the MySQL datadir image."; exit 1
fi

echo "Building Release once so both legs run --no-build..."
if ! dotnet build SchemaSmith.sln -c Release -v q --nologo >/dev/null 2>&1; then
  echo "FAIL: Release build failed."; exit 1
fi

run_leg() {
  local engine="$1" name="$2" port="$3" image="$4" tag="$5" category="$6"
  echo ""
  echo "--- $engine ($image:$tag, port $port) ---"
  docker rm -f "$name" >/dev/null 2>&1

  if [ "$engine" = "mariadb" ]; then
    docker run -d --name "$name" \
      -e MARIADB_ROOT_PASSWORD="$TEST_PASSWORD" -e MARIADB_USER="$TEST_USER" \
      -e MARIADB_PASSWORD="$TEST_PASSWORD" -e MARIADB_DATABASE=TestMain \
      -p "$port:3306" "$image:$tag" >/dev/null
  else
    docker run -d --name "$name" \
      -e MYSQL_ROOT_PASSWORD="$TEST_PASSWORD" -e MYSQL_USER="$TEST_USER" \
      -e MYSQL_PASSWORD="$TEST_PASSWORD" -e MYSQL_DATABASE=TestMain \
      -p "$port:3306" "$image:$tag" >/dev/null
  fi

  # MariaDB 11.x ships the mariadb-named CLIs (mariadb, mariadb-admin) and no longer the mysql-named
  # symlinks; MySQL ships the mysql-named ones. Pick the right binaries per engine (same split the
  # encryption sweep uses) or the readiness ping never succeeds and the leg is skipped as "never ready".
  local admin_cli sql_cli
  if [ "$engine" = "mariadb" ]; then admin_cli="mariadb-admin"; sql_cli="mariadb"; else admin_cli="mysqladmin"; sql_cli="mysql"; fi

  echo -n "  waiting for readiness"
  ready=0
  for _ in $(seq 1 60); do
    docker exec "$name" sh -c "$admin_cli ping -h127.0.0.1 -uroot -p'$TEST_PASSWORD'" >/dev/null 2>&1 && ready=1 && break
    echo -n "."; sleep 2
  done
  echo ""
  if [ "$ready" != "1" ]; then
    echo "  FAIL: $name never became ready."
    SWEEP_ROWS="${SWEEP_ROWS}| $engine @ $port | NEVER READY | n/a |
"
    FAILED=1; return
  fi

  docker exec "$name" sh -c "$sql_cli -uroot -p'$TEST_PASSWORD' -e \"
    GRANT ALL PRIVILEGES ON *.* TO '$TEST_USER'@'%' WITH GRANT OPTION;
    FLUSH PRIVILEGES; SET GLOBAL max_connections = 2000;\"" >/dev/null 2>&1

  # Smoke-check the infra itself: DATA DIRECTORY must actually work against /ddspace, or a green (0-test)
  # run would look like a pass while proving nothing.
  if ! docker exec "$name" sh -c "$sql_cli -uroot -p'$TEST_PASSWORD' -e \"
    CREATE DATABASE IF NOT EXISTS _ddsmoke;
    CREATE TABLE _ddsmoke.t (id INT) ENGINE=InnoDB DATA DIRECTORY='/ddspace';
    DROP DATABASE _ddsmoke;\"" >/dev/null 2>&1; then
    echo "  FAIL: the $engine datadir image cannot create a DATA DIRECTORY='/ddspace' table -- infra is broken, fix before trusting the sweep."
    SWEEP_ROWS="${SWEEP_ROWS}| $engine @ $port | SMOKE CHECK FAILED | n/a |
"
    FAILED=1; return
  fi
  echo "  datadir smoke check passed (DATA DIRECTORY='/ddspace' works)"

  local port_env
  if [ "$engine" = "mariadb" ]; then port_env="SmithySettings_MariaDB__Port=$port"; else port_env="SmithySettings_MySQL__Port=$port"; fi

  out=$(env $port_env dotnet test "$PROJ" -c Release --no-build \
          --filter "TestCategory=DataDirectory&TestCategory=$category" 2>&1)
  summary=$(echo "$out" | grep -aE '^(Passed!|Failed!)' | tail -1)
  total=$(echo "$summary" | grep -oE 'Total:[[:space:]]*[0-9]+' | grep -oE '[0-9]+')

  if [ -z "$summary" ] && echo "$out" | grep -qiE 'No test (matches|is available|matched)'; then
    echo "  NO TESTS: no DataDirectory+$category tests matched. Infra is proven by the smoke check above."
    SWEEP_ROWS="${SWEEP_ROWS}| $engine @ $port | NO TESTS MATCHED | smoke check passed |
"
  elif echo "$summary" | grep -q '^Passed!' && [ "${total:-0}" != "0" ]; then
    echo "  $summary"
    SWEEP_ROWS="${SWEEP_ROWS}| $engine @ $port | $summary | smoke check passed |
"
  else
    FAILED=1
    echo "  ${summary:-<no test summary emitted>}"
    echo "$out" | grep -aE '  Failed [A-Za-z]|Error occurred while kindling' | head -8 | sed 's/^/    /'
    SWEEP_ROWS="${SWEEP_ROWS}| $engine @ $port | ${summary:-NO RESULT} | smoke check passed |
"
  fi
}

run_leg mariadb "$MARIADB_NAME" "$MARIADB_PORT" schemasmith-datadir-mariadb 11.4 MariaDb
run_leg mysql   "$MYSQL_NAME"   "$MYSQL_PORT"   schemasmith-datadir-mysql   8.0  MySQL

# Appended, never rewritten: the history of what was swept is the point (same pattern as
# scripts/run-genuine-sweep.sh's record).
mkdir -p "$(dirname "$RECORD")"
if [ ! -f "$RECORD" ]; then
  {
    echo "# DataDirectory sweep results"
    echo ""
    echo "Appended by \`scripts/run-datadir-sweep.sh\`. CI cannot run this sweep -- the demo/CI containers"
    echo "have no /ddspace directory and MySQL needs it listed in innodb_directories -- so this file is the"
    echo "standing evidence that it ran, and against what."
  } > "$RECORD"
fi
{
  echo ""
  echo "## $SWEEP_STARTED - commit $SWEEP_SHA"
  echo ""
  echo "| Target | Result | Infra |"
  echo "|---|---|---|"
  printf "%s" "$SWEEP_ROWS"
} >> "$RECORD"
echo
echo "Recorded in $RECORD for commit $SWEEP_SHA -- commit it alongside the work it certifies."

echo ""
if [ "$FAILED" = "0" ]; then
  echo "PASS: DataDirectory tests green on both MariaDB and MySQL."
else
  echo "FAIL: at least one DataDirectory leg is red or its infra is broken."
fi
exit $FAILED

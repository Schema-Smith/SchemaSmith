#!/usr/bin/env bash
# Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
#
# Runs the server-settings integration tests (category DisposableServer<engine>) against one throwaway, memory-capped
# container per engine, and on SQL Server the server-object extraction test (DisposableServerObjects, Agent on). What
# the tests change -- above all a setting waiting for a restart -- would outlive them on a shared server, so they never
# run against the demo servers: they skip themselves unless SCHEMASMITH_DISPOSABLE_SERVER=1, which only this script sets.
#
# Usage: bash scripts/run-server-settings-legs.sh            all four engines
#        ENGINES="PostgreSQL MySQL" bash scripts/run-server-settings-legs.sh
#        NO_BUILD=1 ...                                       reuse the current build
#        FLOOR=1 ...                                          the floor images instead of the modern ones
#        MYSQL_IMAGE=mysql:8.0 ...                            any one image (SQLSERVER_, POSTGRES_, MARIADB_IMAGE too)
set -u

PASSWORD='Settings!Leg2026x'
ENGINES=${ENGINES:-"SqlServer PostgreSQL MySQL MariaDb"}
# SQL Server's Linux images start at 2017, so that is the floor a container can reach.
if [ "${FLOOR:-0}" = "1" ]; then
  SQLSERVER_IMAGE=${SQLSERVER_IMAGE:-mcr.microsoft.com/mssql/server:2017-latest}
  POSTGRES_IMAGE=${POSTGRES_IMAGE:-postgres:12}
  MYSQL_IMAGE=${MYSQL_IMAGE:-mysql:5.7}
  MARIADB_IMAGE=${MARIADB_IMAGE:-mariadb:10.2}
fi
SQLSERVER_IMAGE=${SQLSERVER_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}
POSTGRES_IMAGE=${POSTGRES_IMAGE:-postgres:16}
MYSQL_IMAGE=${MYSQL_IMAGE:-mysql:8.4}
MARIADB_IMAGE=${MARIADB_IMAGE:-mariadb:11.4}
PROJ='SchemaQuench/SchemaQuench.IntegrationTests/SchemaQuench.IntegrationTests.csproj'
TONGS='SchemaTongs/SchemaTongs.IntegrationTests/SchemaTongs.IntegrationTests.csproj'
FAILED=0
STARTED=()

cleanup() {
  for name in "${STARTED[@]}"; do docker rm -fv "$name" >/dev/null 2>&1; done
}
trap cleanup EXIT

if [ "${NO_BUILD:-0}" != "1" ]; then
  # A failed build would leave the last good binaries in place, and the legs would quietly test those.
  for proj in "$PROJ" "$TONGS"; do
    if ! out=$(dotnet build "$proj" -c Debug -nologo -v q 2>&1); then
      echo "$out" | grep -E " error " | head -5
      echo "FAIL: $proj did not build -- NOT RUN"
      exit 1
    fi
  done
fi

wait_ready() {
  local name=$1 probe=$2
  for _ in $(seq 1 90); do
    MSYS_NO_PATHCONV=1 docker exec "$name" sh -c "$probe" >/dev/null 2>&1 && return 0
    sleep 2
  done
  return 1
}

for engine in $ENGINES; do
  name="settings-leg-$(echo "$engine" | tr '[:upper:]' '[:lower:]')"
  docker rm -fv "$name" >/dev/null 2>&1
  extra_env=()
  case "$engine" in
    SqlServer)
      port=14450; prefix=SqlServer; user=sa; image=$SQLSERVER_IMAGE
      # SA_PASSWORD for 2017; sqlcmd moved to mssql-tools18 (with -C) from 2022.
      docker run -d --rm --name "$name" --memory 4g --memory-swap 4g -p "$port:1433" -e ACCEPT_EULA=Y \
        -e "MSSQL_SA_PASSWORD=$PASSWORD" -e "SA_PASSWORD=$PASSWORD" -e MSSQL_COLLATION=SQL_Latin1_General_CP1_CS_AS -e MSSQL_AGENT_ENABLED=true \
        "$image" >/dev/null
      # Ready means Agent has started too: it turns 'show advanced options' on and back off to enable Agent XPs, which
      # would race a test's own sp_configure.
      agent="IF (SELECT CAST(value_in_use AS INT) FROM sys.configurations WHERE name = 'Agent XPs') = 1 SELECT 1 ELSE RAISERROR('Agent is starting', 16, 1)"
      probe="/opt/mssql-tools18/bin/sqlcmd -C -b -S localhost -U sa -P '$PASSWORD' -Q \"$agent\" || /opt/mssql-tools/bin/sqlcmd -b -S localhost -U sa -P '$PASSWORD' -Q \"$agent\"" ;;
    PostgreSQL)
      port=14451; prefix=PostgreSQL; user=postgres; image=$POSTGRES_IMAGE
      docker run -d --rm --name "$name" --memory 1g --memory-swap 1g -p "$port:5432" \
        -e POSTGRES_PASSWORD="$PASSWORD" "$image" >/dev/null
      probe="pg_isready -U postgres" ;;
    MySQL)
      port=14452; prefix=MySQL; user=root; image=$MYSQL_IMAGE
      docker run -d --rm --name "$name" --memory 1g --memory-swap 1g -p "$port:3306" \
        -e MYSQL_ROOT_PASSWORD="$PASSWORD" "$image" >/dev/null
      # The mysql:8.0.11 image ships no mysqladmin; the plain client over TCP answers the same question there.
      probe="mysqladmin ping -h 127.0.0.1 -u root -p'$PASSWORD' || mysql -h 127.0.0.1 -u root -p'$PASSWORD' -e 'SELECT 1'"
      # MySQL 5.7's TLS handshake fails with the modern connector on the stock image; CI turns SSL off for it too.
      case "$image" in mysql:5.7*) extra_env+=("SmithySettings_MySQL__ConnectionProperties__SslMode=None") ;; esac ;;
    MariaDb)
      port=14453; prefix=MariaDB; user=root; image=$MARIADB_IMAGE
      # Older images know only the MYSQL_ names, newer ones only the mariadb- tools.
      docker run -d --rm --name "$name" --memory 1g --memory-swap 1g -p "$port:3306" \
        -e MARIADB_ROOT_PASSWORD="$PASSWORD" -e MYSQL_ROOT_PASSWORD="$PASSWORD" "$image" >/dev/null
      probe="mariadb-admin ping -h 127.0.0.1 -u root -p'$PASSWORD' || mysqladmin ping -h 127.0.0.1 -u root -p'$PASSWORD'" ;;
    *) echo "unknown engine $engine"; FAILED=1; continue ;;
  esac
  STARTED+=("$name")

  echo "--- $engine on $port ($image) ---"
  if ! wait_ready "$name" "$probe"; then
    echo "  FAIL: $name never became ready -- NOT RUN"
    FAILED=1; continue
  fi

  run_category() {
    local proj=$1 category=$2
    out=$(env SCHEMASMITH_DISPOSABLE_SERVER=1 \
            "SmithySettings_${prefix}__Server=127.0.0.1" "SmithySettings_${prefix}__Port=$port" \
            "SmithySettings_${prefix}__User=$user" "SmithySettings_${prefix}__Password=$PASSWORD" ${extra_env[@]+"${extra_env[@]}"} \
            dotnet test "$proj" -c Debug --no-build -nologo --filter "TestCategory=$category" 2>&1)
    rc=$?
    summary=$(echo "$out" | grep -aE '^(Passed!|Failed!)' | tail -1)
    # A filter that selects nothing prints no summary: that is a failure, not a pass.
    # Nor is a run where every test skipped itself: dotnet prints "Passed!" with nothing passed.
    passed=$(echo "$summary" | grep -oE 'Passed: +[0-9]+' | grep -oE '[0-9]+')
    if [ "$rc" -eq 0 ] && echo "$summary" | grep -q '^Passed!' && [ "${passed:-0}" -gt 0 ]; then
      echo "  $category: $summary"
    else
      FAILED=1
      echo "  $category: ${summary:-NO RESULT} (exit $rc)"
      echo "$out" | grep -aE -A1 '^\s+Failed |Error Message|Expected|But was' | head -30 | sed 's/^/    /'
    fi
  }
  run_category "$PROJ" "DisposableServer$engine"
  [ "$engine" = SqlServer ] && run_category "$TONGS" DisposableServerObjects

  docker rm -fv "$name" >/dev/null 2>&1
done

[ "$FAILED" = "0" ] && echo "PASS: server settings on every engine." || echo "FAIL: at least one engine is red."
exit $FAILED

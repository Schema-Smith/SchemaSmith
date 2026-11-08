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
set -u

PASSWORD='Settings!Leg2026x'
ENGINES=${ENGINES:-"SqlServer PostgreSQL MySQL MariaDb"}
PROJ='SchemaQuench/SchemaQuench.IntegrationTests/SchemaQuench.IntegrationTests.csproj'
TONGS='SchemaTongs/SchemaTongs.IntegrationTests/SchemaTongs.IntegrationTests.csproj'
FAILED=0
STARTED=()

cleanup() {
  for name in "${STARTED[@]}"; do docker rm -fv "$name" >/dev/null 2>&1; done
}
trap cleanup EXIT

if [ "${NO_BUILD:-0}" != "1" ]; then
  dotnet build "$PROJ" -c Debug -nologo -v q 2>&1 | grep -E " error |Error\(s\)" | head -5
  dotnet build "$TONGS" -c Debug -nologo -v q 2>&1 | grep -E " error |Error\(s\)" | head -5
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
  case "$engine" in
    SqlServer)
      port=14450; prefix=SqlServer; user=sa
      docker run -d --rm --name "$name" --memory 4g --memory-swap 4g -p "$port:1433" -e ACCEPT_EULA=Y \
        -e "MSSQL_SA_PASSWORD=$PASSWORD" -e MSSQL_COLLATION=SQL_Latin1_General_CP1_CS_AS -e MSSQL_AGENT_ENABLED=true \
        mcr.microsoft.com/mssql/server:2022-latest >/dev/null
      probe="/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P '$PASSWORD' -Q 'SELECT 1'" ;;
    PostgreSQL)
      port=14451; prefix=PostgreSQL; user=postgres
      docker run -d --rm --name "$name" --memory 1g --memory-swap 1g -p "$port:5432" \
        -e POSTGRES_PASSWORD="$PASSWORD" postgres:16 >/dev/null
      probe="pg_isready -U postgres" ;;
    MySQL)
      port=14452; prefix=MySQL; user=root
      docker run -d --rm --name "$name" --memory 1g --memory-swap 1g -p "$port:3306" \
        -e MYSQL_ROOT_PASSWORD="$PASSWORD" mysql:8.4 >/dev/null
      probe="mysqladmin ping -h 127.0.0.1 -u root -p'$PASSWORD'" ;;
    MariaDb)
      port=14453; prefix=MariaDB; user=root
      docker run -d --rm --name "$name" --memory 1g --memory-swap 1g -p "$port:3306" \
        -e MARIADB_ROOT_PASSWORD="$PASSWORD" mariadb:11.4 >/dev/null
      probe="mariadb-admin ping -h 127.0.0.1 -u root -p'$PASSWORD'" ;;
    *) echo "unknown engine $engine"; FAILED=1; continue ;;
  esac
  STARTED+=("$name")

  echo "--- $engine on $port ---"
  if ! wait_ready "$name" "$probe"; then
    echo "  FAIL: $name never became ready -- NOT RUN"
    FAILED=1; continue
  fi

  run_category() {
    local proj=$1 category=$2
    out=$(env SCHEMASMITH_DISPOSABLE_SERVER=1 \
            "SmithySettings_${prefix}__Server=127.0.0.1" "SmithySettings_${prefix}__Port=$port" \
            "SmithySettings_${prefix}__User=$user" "SmithySettings_${prefix}__Password=$PASSWORD" \
            dotnet test "$proj" -c Debug --no-build -nologo --filter "TestCategory=$category" 2>&1)
    rc=$?
    summary=$(echo "$out" | grep -aE '^(Passed!|Failed!)' | tail -1)
    # A filter that selects nothing prints no summary: that is a failure, not a pass.
    if [ "$rc" -eq 0 ] && echo "$summary" | grep -q '^Passed!'; then
      echo "  $category: $summary"
    else
      FAILED=1
      echo "  $category: ${summary:-NO RESULT} (exit $rc)"
      echo "$out" | grep -aE '^\s+Failed |Error Message|Expected|But was|Exception' | head -20 | sed 's/^/    /'
    fi
  }
  run_category "$PROJ" "DisposableServer$engine"
  [ "$engine" = SqlServer ] && run_category "$TONGS" DisposableServerObjects

  docker rm -fv "$name" >/dev/null 2>&1
done

[ "$FAILED" = "0" ] && echo "PASS: server settings on every engine." || echo "FAIL: at least one engine is red."
exit $FAILED

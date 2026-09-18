#!/usr/bin/env bash
# Paths below are repo-relative; this anchors the script so it runs from anywhere.
cd "$(dirname "$0")/.." || exit 1
# Version-FLOOR sweep (pre-PR gate). LOCAL-ONLY convenience: stands up the oldest supported engine of
# each family with CI-identical credentials, runs that engine's integration category against it, and
# reports.
#
# WHY THIS EXISTS. The demo containers everyone runs the gate against are all MODERN versions, so a green
# four-engine run says nothing about the supported floors. On 2026-09-02 four version-floor defects reached
# CI that way -- two of them killed kindling outright, so ZERO tests ran on MariaDB 10.2 and MySQL 5.7
# while the local gate reported 5,337 passing. It took six CI runs to land one PR. Every one of those
# defects reproduces here in under two minutes.
#
# NOT the learn-* sandbox containers: those are training-lab servers with no TestUser, and pointing the
# suite at them fails with "Access denied" that looks like a product problem.
#
# THE BUG FAMILY THIS CATCHES: a version-specific catalog column, system variable, or SQL construct
# referenced where the engine cannot resolve it. On MySQL/MariaDB that resolution happens at CREATE
# PROCEDURE time, so a runtime version guard does NOT protect you -- the mention alone is fatal and takes
# the whole kindle down. The fix is to name it only inside a string literal and PREPARE/EXECUTE it.
#
# Versions mirror continuous-integration.yml's matrices; keep them in sync when CI's floors move.
set -u

TEST_USER='TestUser'
TEST_PASSWORD='aCa2d805-41E5@40c4!98e7#92F93zzxo176'
# All four, as CI's floor legs run them. Schema.IntegrationTests alone catches a kindle that dies, but the
# PR #392 family (MySQL 5.7 parenthesized DEFAULT, SRS_ID, comment escaping) only fails in the tool suites.
PROJS=(
  'Schema/Schema.IntegrationTests/Schema.IntegrationTests.csproj'
  'SchemaQuench/SchemaQuench.IntegrationTests/SchemaQuench.IntegrationTests.csproj'
  'SchemaTongs/SchemaTongs.IntegrationTests/SchemaTongs.IntegrationTests.csproj'
  'DataTongs/DataTongs.IntegrationTests/DataTongs.IntegrationTests.csproj'
)
KEEP=${KEEP_CONTAINERS:-0}
FAILED=0

# name:image:host-port:category
FLOORS=(
  "floor-mariadb-102:mariadb:10.2:13402:MariaDb"
  "floor-mariadb-106:mariadb:10.6:13406:MariaDb"
  "floor-mysql-57:mysql:5.7:13457:MySQL"
  "floor-postgres-12:postgres:12:15412:PostgreSQL"
)

cleanup() {
  [ "$KEEP" = "1" ] && { echo "KEEP_CONTAINERS=1 -- leaving floor containers up"; return; }
  for f in "${FLOORS[@]}"; do docker rm -f "${f%%:*}" >/dev/null 2>&1; done
}
trap cleanup EXIT

echo "===== Version-floor sweep ====="
echo "Building Release once so every leg runs --no-build..."
if ! dotnet build SchemaSmith.sln -c Release -v q --nologo >/dev/null 2>&1; then
  echo "FAIL: Release build failed. Fix that first -- the legs below would all fail for the same reason."
  exit 1
fi

for f in "${FLOORS[@]}"; do
  IFS=':' read -r name image tag port category <<< "$f"
  echo ""
  echo "--- $image:$tag  (port $port, category $category) ---"
  docker rm -f "$name" >/dev/null 2>&1

  if [ "$image" = "postgres" ]; then
    docker run -d --name "$name" \
      -e POSTGRES_USER="$TEST_USER" -e POSTGRES_PASSWORD="$TEST_PASSWORD" -e POSTGRES_DB=TestMain \
      -p "$port:5432" "$image:$tag" >/dev/null
  else
    docker run -d --name "$name" \
      -e MYSQL_ROOT_PASSWORD="$TEST_PASSWORD" -e MYSQL_USER="$TEST_USER" \
      -e MYSQL_PASSWORD="$TEST_PASSWORD" -e MYSQL_DATABASE=TestMain \
      -p "$port:3306" "$image:$tag" >/dev/null
  fi

  # Wait for readiness rather than sleeping a guessed interval -- 5.7 and 10.2 differ by ~20s.
  echo -n "  waiting for readiness"
  ready=0
  for _ in $(seq 1 60); do
    if [ "$image" = "postgres" ]; then
      docker exec "$name" pg_isready -U "$TEST_USER" -d TestMain >/dev/null 2>&1 && ready=1 && break
    else
      docker exec "$name" sh -c "mysqladmin ping -h 127.0.0.1 -u root -p'$TEST_PASSWORD'" >/dev/null 2>&1 && ready=1 && break
    fi
    echo -n "."; sleep 2
  done
  echo ""
  if [ "$ready" != "1" ]; then
    echo "  FAIL: $name never became ready."
    FAILED=1; continue
  fi

  # MySQL/MariaDB images create TestUser without global rights; CI grants them in its own step.
  if [ "$image" != "postgres" ]; then
    docker exec "$name" sh -c "mysql -u root -p'$TEST_PASSWORD' -e \"
      GRANT ALL PRIVILEGES ON *.* TO '$TEST_USER'@'%' WITH GRANT OPTION;
      FLUSH PRIVILEGES;
      SET GLOBAL max_connections = 2000;\"" >/dev/null 2>&1
  fi

  # MySQL 5.7's TLS handshake is incompatible with the modern connector's negotiation on the stock image
  # (SSL Authentication Error / corrupted frame). CI disables SSL for that leg only; mirror it.
  SSL_ENV=""
  [ "$image:$tag" = "mysql:5.7" ] && SSL_ENV="SmithySettings_MySQL__ConnectionProperties__SslMode=None"

  case "$category" in
    MariaDb)    PORT_ENV="SmithySettings_MariaDB__Port=$port" ;;
    MySQL)      PORT_ENV="SmithySettings_MySQL__Port=$port" ;;
    PostgreSQL) PORT_ENV="SmithySettings_PostgreSQL__Port=$port" ;;
  esac

  for proj in "${PROJS[@]}"; do
    out=$(env $PORT_ENV $SSL_ENV dotnet test "$proj" -c Release --no-build \
            --filter "TestCategory=$category" 2>&1)
    summary=$(echo "$out" | grep -aE '^(Passed!|Failed!)' | tail -1)

    # A missing summary is a failure, not a pass: a filter that selects nothing prints no "Passed!" line.
    if echo "$summary" | grep -q '^Passed!'; then
      echo "  $(basename "$proj" .csproj): $summary"
    else
      FAILED=1
      echo "  $(basename "$proj" .csproj): ${summary:-NO RESULT}"
      echo "$out" | grep -aE '  Failed [A-Za-z]|Error occurred while kindling|Unknown column|Unknown system variable|error in your SQL syntax' | head -8 | sed 's/^/    /'
    fi
  done
done

echo ""
if [ "$FAILED" = "0" ]; then
  echo "PASS: every version floor is green."
  echo "Run scripts/check-sweep-record.sh and the pre-push lint too -- this covers only the engine floors."
else
  echo "FAIL: at least one floor is red. Fix before pushing; a CI round-trip costs a full three-engine matrix."
fi
exit $FAILED

#!/usr/bin/env bash
# Paths below are repo-relative; this anchors the script so it runs from anywhere.
cd "$(dirname "$0")/.." || exit 1
# lower_case_table_names=1 legs (pre-PR gate). LOCAL-ONLY convenience mirroring CI's integration-tests-lctn1 job:
# MySQL 8.0 runs on every pull request, MariaDB 11.4 on merge to main, and both run here.
#
# WHY THIS EXISTS. Every other leg, local and CI, runs the Linux default lower_case_table_names=0, so a name
# compared by its declared spelling passes them all and fails only here: the server stores database and table
# names in lowercase. PR #583 shipped a silently skipped index change this way, caught only by CI.
#
# The server argument is the point, so the check that it took is not optional: a container that ignored it
# would run a green leg that tested nothing this leg exists for.
set -u

TEST_USER='TestUser'
TEST_PASSWORD='aCa2d805-41E5@40c4!98e7#92F93zzxo176'
PROJS=(
  'Schema/Schema.IntegrationTests/Schema.IntegrationTests.csproj'
  'SchemaQuench/SchemaQuench.IntegrationTests/SchemaQuench.IntegrationTests.csproj'
  'SchemaTongs/SchemaTongs.IntegrationTests/SchemaTongs.IntegrationTests.csproj'
  'DataTongs/DataTongs.IntegrationTests/DataTongs.IntegrationTests.csproj'
)
KEEP=${KEEP_CONTAINERS:-0}
FAILED=0

# name:image:tag:host-port:category:env-prefix -- the versions continuous-integration.yml's lctn1 matrix pins.
LEGS=(
  "lctn1-mysql-80:mysql:8.0:13581:MySQL:MYSQL"
  "lctn1-mariadb-114:mariadb:11.4:13515:MariaDb:MARIADB"
)

cleanup() {
  [ "$KEEP" = "1" ] && { echo "KEEP_CONTAINERS=1 -- leaving lctn1 containers up"; return; }
  for l in "${LEGS[@]}"; do docker rm -fv "${l%%:*}" >/dev/null 2>&1; done
}
trap cleanup EXIT

echo "===== lower_case_table_names=1 legs ====="
echo "Building Release once so every leg runs --no-build..."
if ! dotnet build SchemaSmith.sln -c Release -v q --nologo >/dev/null 2>&1; then
  echo "FAIL: Release build failed."
  exit 1
fi

for l in "${LEGS[@]}"; do
  IFS=':' read -r name image tag port category prefix <<< "$l"
  echo ""
  echo "--- $image:$tag lower_case_table_names=1 (port $port, category $category) ---"
  docker rm -fv "$name" >/dev/null 2>&1
  docker run -d --name "$name" --memory 2g --memory-swap 2g \
    -e "${prefix}_ROOT_PASSWORD=$TEST_PASSWORD" -e "${prefix}_USER=$TEST_USER" \
    -e "${prefix}_PASSWORD=$TEST_PASSWORD" -e "${prefix}_DATABASE=TestMain" \
    -p "$port:3306" "$image:$tag" --lower-case-table-names=1 >/dev/null

  # MariaDB 11.4 ships mariadb/mariadb-admin and not the mysql* names; try both.
  CLI="{ command -v mariadb >/dev/null && C=mariadb || C=mysql; } ; \$C -u root -p'$TEST_PASSWORD' -N"
  echo -n "  waiting for readiness"
  ready=0
  for _ in $(seq 1 60); do
    docker exec "$name" sh -c "$CLI -e 'SELECT 1'" >/dev/null 2>&1 && ready=1 && break
    echo -n "."; sleep 2
  done
  echo ""
  if [ "$ready" != "1" ]; then
    echo "  FAIL: $name never became ready."
    FAILED=1; continue
  fi

  lctn=$(docker exec "$name" sh -c "$CLI -e \"GRANT ALL PRIVILEGES ON *.* TO '$TEST_USER'@'%' WITH GRANT OPTION; FLUSH PRIVILEGES; SET GLOBAL max_connections = 2000; SELECT @@lower_case_table_names;\"" 2>/dev/null | tail -1)
  if [ "$lctn" != "1" ]; then
    echo "  FAIL: the server reports lower_case_table_names=${lctn:-?}, not 1 -- this leg would test nothing."
    FAILED=1; continue
  fi

  case "$category" in
    MariaDb) PORT_ENV="SmithySettings_MariaDB__Port=$port" ;;
    MySQL)   PORT_ENV="SmithySettings_MySQL__Port=$port" ;;
  esac

  for proj in "${PROJS[@]}"; do
    out=$(env $PORT_ENV dotnet test "$proj" -c Release --no-build --filter "TestCategory=$category" 2>&1)
    rc=$?
    summary=$(echo "$out" | grep -aE '^(Passed!|Failed!)' | tail -1)
    # No summary means nothing ran, which is a failure, as is a non-zero exit under "Passed!" (a teardown error).
    if [ "$rc" -eq 0 ] && echo "$summary" | grep -q '^Passed!'; then
      echo "  $(basename "$proj" .csproj): $summary"
    else
      FAILED=1
      echo "  $(basename "$proj" .csproj): ${summary:-NO RESULT} (exit $rc)"
      echo "$out" | grep -aE '  Failed [A-Za-z]|Error occurred while kindling|TearDown failed|SetUp failed' | head -8 | sed 's/^/    /'
    fi
  done

  [ "$KEEP" != "1" ] && docker rm -fv "$name" >/dev/null 2>&1
done

echo ""
if [ "$FAILED" = "0" ]; then
  echo "PASS: both lower_case_table_names=1 legs are green."
else
  echo "FAIL: a lower_case_table_names=1 leg is red."
fi
exit $FAILED

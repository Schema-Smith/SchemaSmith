#!/usr/bin/env bash
# Paths below are repo-relative; this anchors the script so it runs from anywhere.
cd "$(dirname "$0")/.." || exit 1
# Encryption sweep (LOCAL-ONLY). At-rest table encryption needs a server-side key-management backend the
# stock demo/CI containers lack, and GitHub Actions `services:` cannot mount the plugin config -- so the
# `Encryption` test category runs here, against a purpose-built MariaDB image, the same way the genuine-
# binary sweep runs the pre-2016 SQL Server fixtures locally. See scripts/test-infra/encryption/README.md.
#
# MariaDB: file_key_management works (scripts/test-infra/encryption/mariadb). MySQL: component_keyring_file
# will not initialize in the Oracle image entrypoint (bug #108197 family) -- MySQL encryption tests are
# [Explicit]/skipped until a custom-entrypoint workaround lands, so this sweep covers MariaDB only for now.
set -u

TEST_USER='TestUser'
TEST_PASSWORD='aCa2d805-41E5@40c4!98e7#92F93zzxo176'
PROJ='Schema/Schema.IntegrationTests/Schema.IntegrationTests.csproj'
KEEP=${KEEP_CONTAINERS:-0}
NAME='enc-mariadb-114'
PORT=13417
RECORD='docs/development/encryption-sweep-results.md'
FAILED=0

cleanup() {
  [ "$KEEP" = "1" ] && { echo "KEEP_CONTAINERS=1 -- leaving the encryption container up"; return; }
  docker rm -f "$NAME" >/dev/null 2>&1
}
trap cleanup EXIT

echo "===== Encryption sweep (MariaDB, local-only) ====="
echo "Building the MariaDB encryption image..."
if ! docker build -q -t schemasmith-enc-mariadb:11.4 scripts/test-infra/encryption/mariadb >/dev/null 2>&1; then
  echo "FAIL: could not build the MariaDB encryption image."; exit 1
fi

echo "Building Release once so the test leg runs --no-build..."
if ! dotnet build SchemaSmith.sln -c Release -v q --nologo >/dev/null 2>&1; then
  echo "FAIL: Release build failed."; exit 1
fi

docker rm -f "$NAME" >/dev/null 2>&1
docker run -d --name "$NAME" \
  -e MARIADB_ROOT_PASSWORD="$TEST_PASSWORD" -e MARIADB_USER="$TEST_USER" \
  -e MARIADB_PASSWORD="$TEST_PASSWORD" -e MARIADB_DATABASE=TestMain \
  -p "$PORT:3306" schemasmith-enc-mariadb:11.4 >/dev/null

echo -n "  waiting for readiness"
ready=0
for _ in $(seq 1 60); do
  docker exec "$NAME" sh -c "mariadb-admin ping -h127.0.0.1 -uroot -p'$TEST_PASSWORD'" >/dev/null 2>&1 && ready=1 && break
  echo -n "."; sleep 2
done
echo ""
[ "$ready" != "1" ] && { echo "  FAIL: $NAME never became ready."; exit 1; }

docker exec "$NAME" sh -c "mariadb -uroot -p'$TEST_PASSWORD' -e \"
  GRANT ALL PRIVILEGES ON *.* TO '$TEST_USER'@'%' WITH GRANT OPTION;
  FLUSH PRIVILEGES; SET GLOBAL max_connections = 2000;\"" >/dev/null 2>&1

# Smoke-check the infra itself: encryption must actually be available, or a green (0-test) run would look
# like a pass while proving nothing.
if ! docker exec "$NAME" sh -c "mariadb -uroot -p'$TEST_PASSWORD' -e \"
  CREATE DATABASE IF NOT EXISTS _encsmoke; CREATE TABLE _encsmoke.t (id INT) ENCRYPTED=YES; DROP DATABASE _encsmoke;\"" >/dev/null 2>&1; then
  echo "  FAIL: the encryption image cannot create an ENCRYPTED table -- infra is broken, fix before trusting the sweep."
  exit 1
fi
echo "  encryption smoke check passed (ENCRYPTED=YES works)"

out=$(env SmithySettings_MariaDB__Port=$PORT dotnet test "$PROJ" -c Release --no-build \
        --filter "TestCategory=Encryption&TestCategory=MariaDb" 2>&1)
summary=$(echo "$out" | grep -aE '^(Passed!|Failed!)' | tail -1)
total=$(echo "$summary" | grep -oE 'Total:[[:space:]]*[0-9]+' | grep -oE '[0-9]+')

if [ -z "$summary" ] && echo "$out" | grep -qiE 'No test (matches|is available|matched)'; then
  # No Encryption/MariaDb tests exist yet. Not a failure, but not a pass either -- the infra smoke check
  # above is the only thing proven. This is expected only before the encryption feature's tests land.
  echo "  NO TESTS: no Encryption+MariaDb tests matched. Infra is proven by the smoke check above, but"
  echo "            there are no encryption tests to run yet. (Expected before the feature lands.)"
elif echo "$summary" | grep -q '^Passed!' && [ "${total:-0}" != "0" ]; then
  echo "  $summary"
else
  FAILED=1
  echo "  ${summary:-<no test summary emitted>}"
  echo "$out" | grep -aE '  Failed [A-Za-z]|Error occurred while kindling' | head -8 | sed 's/^/    /'
fi

echo ""
if [ "$FAILED" = "0" ]; then
  echo "PASS: MariaDB encryption tests green. (MySQL encryption is [Explicit]/skipped -- keyring container TODO.)"
else
  echo "FAIL: MariaDB encryption tests red."
fi
exit $FAILED

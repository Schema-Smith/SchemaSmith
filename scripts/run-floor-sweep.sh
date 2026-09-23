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
# READINESS PROBES MUST NOT ASSUME THE mysql* CLIENT NAMES. MariaDB 11.4 and later ship mariadb and
# mariadb-admin and no longer provide the mysql/mysqladmin aliases, so a probe hard-coded to mysqladmin
# reports "never became ready" against a server that is up and serving. Three MariaDB bands -- 11.4, 11.8
# and latest -- ran ZERO tests that way while the sweep looked like it had covered them and failed them.
# That is the same shape as the defect this whole script exists to catch: the harness agreeing with itself
# rather than with the engine.
#
# NOT the learn-* sandbox containers: those are training-lab servers with no TestUser, and pointing the
# suite at them fails with "Access denied" that looks like a product problem.
#
# THE BUG FAMILY THIS CATCHES: a version-specific catalog column, system variable, or SQL construct
# referenced where the engine cannot resolve it. On MySQL/MariaDB that resolution happens at CREATE
# PROCEDURE time, so a runtime version guard does NOT protect you -- the mention alone is fatal and takes
# the whole kindle down. The fix is to name it only inside a string literal and PREPARE/EXECUTE it.
#
# SCOPE CHANGED 2026-09-20: this now sweeps EVERY CI leg, not just the floors, and the reason is that a
# pull request no longer runs every leg. Since the matrix-shape change a PR runs floor + latest per engine
# while merge to main runs the full matrix, and the mitigation Paul accepted for that trade was explicit:
# *"We should be running everything locally before a PR anyway."* If this script still covered only the
# floors, that mitigation would be hollow for precisely the legs CI stopped running on a PR -- PostgreSQL
# 17/16/15/14/13, MySQL 8.4/8.0, MariaDB 11.8/11.4/10.6. Those are now here.
#
# RUNTIME IS HOURS, NOT MINUTES, and that is a deliberate trade rather than an oversight. The floors alone
# took ~25 minutes; the full set is roughly four times that. Run it before opening a PR, not on every
# commit. If that is too slow to actually get run, the honest fix is to shrink the merge matrix -- NOT to
# quietly stop covering what CI no longer covers on the PR, which is how the gap this closes was created.
#
# SQL Server is NOT here: its bands need full-text install and semantic-DB provisioning, which
# scripts/run-modern-band-sweep.sh already does for 2017/2022/2025. The two together cover the matrix.
#
# Versions mirror continuous-integration.yml's matrices; keep them in sync when CI's legs move. `latest`
# is deliberately floating in both places -- it is the tripwire for an engine release breaking us.
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

# name:image:host-port:category -- every leg continuous-integration.yml runs on merge to main, floors
# first so the fastest-failing and highest-signal bands report before the long tail.
#
# EVERY BAND IS AN EXPLICIT MAJOR -- no floating `latest` band. Running `latest` as its own band tests
# whatever the vendor shipped this morning and records a result nobody can reproduce, and when it
# happens to resolve to a major already pinned here it is simply the same run twice. What actually
# needs checking is that the pinned set REACHES the ceiling, which is an assertion, not a test run:
# assert_latest_is_covered() below resolves each engine's `latest` and fails the sweep if its major
# is missing from the list. When an engine ratchets, that assertion is what tells you to add a band.
#
# LOCAL COVERS MORE THAN CI, NEVER LESS. CI minutes are spent on every push, so its matrix is what we
# can afford continuously; this sweep runs once per release and can afford the bands CI cannot. Three
# of these have no CI leg at all and are here for that reason: mysql:9 and mysql:26 sit in the gap
# between the 8.4 LTS leg and the floating latest, and mariadb:12 sits between 11.8 and 13. A floating
# `latest` ROTATES -- the day MySQL 27 ships, nothing tests 26 any more -- so without these the middle
# of a range we document as continuous is exactly where nobody looks. Adding a CI leg for them is a
# separate decision with a per-push cost; covering them here costs one release's wall clock.
FLOORS=(
  "floor-mariadb-102:mariadb:10.2:13402:MariaDb"
  "floor-mysql-57:mysql:5.7:13457:MySQL"
  "floor-postgres-12:postgres:12:15412:PostgreSQL"
  "band-mariadb-106:mariadb:10.6:13406:MariaDb"
  "band-mariadb-114:mariadb:11.4:13414:MariaDb"
  "band-mariadb-118:mariadb:11.8:13418:MariaDb"
  "band-mariadb-12:mariadb:12:13412:MariaDb"
  "band-mariadb-13:mariadb:13:13413:MariaDb"
  "band-mysql-80:mysql:8.0:13480:MySQL"
  "band-mysql-84:mysql:8.4:13484:MySQL"
  "band-mysql-9:mysql:9:13409:MySQL"
  "band-mysql-26:mysql:26:13426:MySQL"
  "band-postgres-13:postgres:13:15413:PostgreSQL"
  "band-postgres-14:postgres:14:15414:PostgreSQL"
  "band-postgres-15:postgres:15:15415:PostgreSQL"
  "band-postgres-16:postgres:16:15416:PostgreSQL"
  "band-postgres-17:postgres:17:15417:PostgreSQL"
  "band-postgres-18:postgres:18:15418:PostgreSQL"
)

cleanup() {
  [ "$KEEP" = "1" ] && { echo "KEEP_CONTAINERS=1 -- leaving floor containers up"; return; }
  for f in "${FLOORS[@]}"; do docker rm -f "${f%%:*}" >/dev/null 2>&1; done
}
trap cleanup EXIT

# Assert the pinned set REACHES the ceiling, rather than spending a band on `latest`. Resolving it is
# a version query, not a test run: if `latest` has moved to a major nothing here pins, that is the
# finding, and the answer is to add a band -- not to let a floating band quietly test it once and
# record a result nobody can reproduce.
assert_latest_is_covered() {
  local img="$1" ver major
  ver=$(docker run --rm "$img:latest" sh -c 'mysqld --version 2>/dev/null || mariadbd --version 2>/dev/null || postgres --version 2>/dev/null' 2>/dev/null | grep -oE "[0-9]+\.[0-9]+(\.[0-9]+)?" | head -1)
  if [ -z "$ver" ]; then
    echo "  !! could not resolve $img:latest -- treating as a FAILURE, an unresolved ceiling is the"
    echo "     exact condition this check exists to catch"
    FAILED=1; return
  fi
  major="${ver%%.*}"
  # MySQL and MariaDB pin whole majors (9, 26, 13); PostgreSQL does too (18). A band matching either
  # the bare major or major.minor counts as covering it.
  if printf '%s
' "${FLOORS[@]}" | grep -qE ":$img:($major|$major\.[0-9]+):"; then
    echo "  $img:latest = $ver -- covered by a pinned band"
  else
    echo "  !! $img:latest = $ver and NO band pins major $major."
    echo "     The ceiling moved. Add \"band-$img-$major:$img:$major:<port>:<category>\" to FLOORS,"
    echo "     and a matching leg to continuous-integration.yml + release.yml REQUIRED_CHECKS."
    FAILED=1
  fi
}

echo "===== Version-floor sweep ====="
echo "--- ceiling check: is each engine's latest covered by a pinned band? ---"
for img in mysql mariadb postgres; do assert_latest_is_covered "$img"; done
echo "Building Release once so every leg runs --no-build..."
if ! dotnet build SchemaSmith.sln -c Release -v q --nologo >/dev/null 2>&1; then
  echo "FAIL: Release build failed. Fix that first -- the legs below would all fail for the same reason."
  exit 1
fi

# FLOOR_FILTER runs a SUBSET by band name, comma-separated:
#   FLOOR_FILTER=band-mysql-9,band-mariadb-12 bash scripts/run-floor-sweep.sh
# For re-running the bands a change actually affects instead of the whole list. The full sweep is
# still what certifies a release -- this exists so that "I added two bands" does not mean re-running
# sixteen that already passed on the same commit.
for f in "${FLOORS[@]}"; do
  IFS=':' read -r name image tag port category <<< "$f"
  if [ -n "${FLOOR_FILTER:-}" ] && ! printf '%s' ",$FLOOR_FILTER," | grep -q ",$name,"; then
    continue
  fi
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
      docker exec "$name" sh -c "{ command -v mariadb-admin >/dev/null && P=mariadb-admin || P=mysqladmin; } ; \$P ping -h 127.0.0.1 -u root -p'$TEST_PASSWORD'" >/dev/null 2>&1 && ready=1 && break
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
    # MariaDB 11.4+ images ship mariadb/mariadb-admin and NO LONGER ship the mysql* names. Try the
    # MariaDB spelling first and fall back, so one loop covers 10.2 through latest and MySQL too.
    docker exec "$name" sh -c "{ command -v mariadb >/dev/null && CLI=mariadb || CLI=mysql; } ; \$CLI -u root -p'$TEST_PASSWORD' -e \"
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

  # Release the band before starting the next one. The cleanup trap still catches everything on exit;
  # this is about not holding sixteen engines resident at once.
  if [ "$KEEP" != "1" ]; then
    docker rm -f "$name" >/dev/null 2>&1
  fi
done

echo ""
if [ "$FAILED" = "0" ]; then
  echo "PASS: every version floor is green."
  echo "Run scripts/check-sweep-record.sh and the pre-push lint too -- this covers only the engine floors."
else
  echo "FAIL: at least one floor is red. Fix before pushing; a CI round-trip costs a full three-engine matrix."
fi
exit $FAILED

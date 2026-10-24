#!/usr/bin/env bash
# Paths below are repo-relative; this anchors the script so it runs from anywhere.
cd "$(dirname "$0")/.." || exit 1
# CI's SSL-off integration legs, run ON LINUX (pre-PR gate). LOCAL-ONLY convenience: stands up MySQL 5.7 and
# MariaDB 10.2 exactly as continuous-integration.yml does, builds and tests the committed HEAD inside a .NET SDK
# container on the engine's network, and reports.
#
# WHY THIS EXISTS. Every other local gate runs the tests on Windows, and these two legs behave differently on
# Linux: CI runs them with SslMode=None because the connector's TLS handshake with those stock images fails
# there ("SSL Authentication Error", surfacing as "Lost connection"), while on Windows it succeeds. So a test
# that rebuilds its settings and drops CI's ConnectionProperties passes every Windows run and fails CI -- which is
# exactly how a ready-for-review PR went red on MySQL 5.7 twice. Both failures reproduce here, and their fixes
# pass here.
#
# WHAT IT TESTS: the committed HEAD, via `git archive` (or LEG_REF=<commit> to test another commit). Uncommitted
# changes are NOT in the run (the script warns).
# Never mount the working tree instead: a Linux build writes bin/ and obj/ that the Windows build then trips on.
#
# NETWORK: the SDK container joins the engine container's network namespace (--network container:<engine>),
# because several fixtures hard-code 127.0.0.1. A container hostname or a published port does not reach them.
# Inside that namespace the engine listens on 3306, so the MariaDB leg overrides the suite's 3317 default.
#
# COST: ~12 minutes per leg, most of it the build. NuGet packages are cached in the named volume
# schemasmith-linux-leg-nuget so later runs skip the restore; `docker volume rm schemasmith-linux-leg-nuget`
# reclaims it. Containers are memory-capped and removed with their volumes on exit.
#
# Usage: scripts/run-linux-leg.sh [mysql57] [mariadb102] [mysql80lctn1] [mariadb114lctn1]     (no argument = the first two)
#
# The lctn1 legs are CI's lower_case_table_names=1 job. They belong here rather than on the Windows host: a test that
# names a file after a database passes on NTFS whatever the case and fails CI's ext4 when the server stores the name in
# lowercase (a checkpoint the deploy never found, PR #592). scripts/run-lctn1-legs.sh runs them through this script.
# Exit status is non-zero if any leg's `dotnet test` exits non-zero -- not only on a failed-test summary, since a
# teardown error exits 1 under a "Passed!" line.
set -u

TEST_USER='TestUser'
TEST_PASSWORD='aCa2d805-41E5@40c4!98e7#92F93zzxo176'
SDK_IMAGE='mcr.microsoft.com/dotnet/sdk:10.0'
NUGET_VOLUME='schemasmith-linux-leg-nuget'

legs=("$@")
[ ${#legs[@]} -eq 0 ] && legs=(mysql57 mariadb102)

stamp=$(date +%Y%m%d-%H%M%S)
results="TestResults/linux-leg/$stamp"
mkdir -p "$results"
work=$(mktemp -d)
engines=()

cleanup() {
  for e in "${engines[@]}"; do docker rm -fv "$e" >/dev/null 2>&1; done
  rm -rf "$work"
}
trap cleanup EXIT

ref=${LEG_REF:-HEAD}
if [ "$ref" = HEAD ] && [ -n "$(git status --porcelain --untracked-files=no)" ]; then
  echo "WARNING: uncommitted changes are NOT tested -- this run tests HEAD ($(git rev-parse --short HEAD))."
fi
echo "Testing $(git rev-parse --short "$ref") on Linux"
git archive -o "$work/head.tar" "$ref" || { echo "git archive failed"; exit 1; }

# The container side: build once, then the four integration projects filtered to the leg's category -- the same
# projects and filter CI's job runs.
cat > "$work/runner.sh" <<'RUNNER'
#!/bin/bash
set -u
mkdir -p /w && tar -xf /in/head.tar -C /w && cd /w
if ! dotnet build SchemaSmith.sln -c Debug -v q --nologo > /out/build.log 2>&1; then
  echo "BUILD FAILED"; grep -E " error " /out/build.log | head -20; exit 1
fi
status=0
for p in Schema/Schema.IntegrationTests SchemaQuench/SchemaQuench.IntegrationTests \
         SchemaTongs/SchemaTongs.IntegrationTests DataTongs/DataTongs.IntegrationTests; do
  n=$(basename "$p")
  dotnet test "$p/$n.csproj" --no-build -c Debug --filter "Category=$1" > "/out/$n.log" 2>&1
  rc=$?
  echo "  $n (exit $rc): $(grep -E '^(Passed!|Failed!)' "/out/$n.log" | tail -1)"
  grep -E '^  Failed |TearDown failed|SetUp failed' "/out/$n.log" | head -10 | sed 's/^/    /'
  [ $rc -eq 0 ] || status=1
done
exit $status
RUNNER

overall=0
for leg in "${legs[@]}"; do
  args=()
  case "$leg" in
    mysql57)    image='mysql:5.7';    category='MySQL'
                envs=(-e SmithySettings_MySQL__ConnectionProperties__SslMode=None)
                dbenv=(-e MYSQL_ROOT_PASSWORD="$TEST_PASSWORD" -e MYSQL_USER="$TEST_USER" -e MYSQL_PASSWORD="$TEST_PASSWORD" -e MYSQL_DATABASE=TestMain) ;;
    mariadb102) image='mariadb:10.2'; category='MariaDb'
                envs=(-e SmithySettings_MariaDB__ConnectionProperties__SslMode=None -e SmithySettings_MariaDB__Port=3306)
                # 10.2 predates the MARIADB_* variables; CI's MARIADB_* names are honoured from 10.3 on.
                dbenv=(-e MYSQL_ROOT_PASSWORD="$TEST_PASSWORD" -e MYSQL_USER="$TEST_USER" -e MYSQL_PASSWORD="$TEST_PASSWORD" -e MYSQL_DATABASE=TestMain) ;;
    mysql80lctn1)    image='mysql:8.0';    category='MySQL'
                     envs=()
                     dbenv=(-e MYSQL_ROOT_PASSWORD="$TEST_PASSWORD" -e MYSQL_USER="$TEST_USER" -e MYSQL_PASSWORD="$TEST_PASSWORD" -e MYSQL_DATABASE=TestMain)
                     args=(--lower-case-table-names=1) ;;
    mariadb114lctn1) image='mariadb:11.4'; category='MariaDb'
                     envs=(-e SmithySettings_MariaDB__Port=3306)
                     dbenv=(-e MARIADB_ROOT_PASSWORD="$TEST_PASSWORD" -e MARIADB_USER="$TEST_USER" -e MARIADB_PASSWORD="$TEST_PASSWORD" -e MARIADB_DATABASE=TestMain)
                     args=(--lower-case-table-names=1) ;;
    *) echo "Unknown leg '$leg' (expected mysql57, mariadb102, mysql80lctn1 or mariadb114lctn1)"; overall=1; continue ;;
  esac

  engine="linux-leg-$leg-$stamp"
  engines+=("$engine")
  out="$results/$leg"; mkdir -p "$out"
  echo "== $leg ($image)"
  docker run -d --name "$engine" --memory 2g --memory-swap 2g "${dbenv[@]}" "$image" "${args[@]}" >/dev/null || { overall=1; continue; }

  cli='{ command -v mariadb >/dev/null && C=mariadb || C=mysql; } ; $C -h 127.0.0.1 -u root -p"$0" -N "$@"'
  ready=0
  for _ in $(seq 1 60); do
    docker exec "$engine" sh -c "$cli" "$TEST_PASSWORD" -e "SELECT 1" >/dev/null 2>&1 && ready=1 && break
    sleep 5
  done
  if [ $ready -ne 1 ]; then echo "  never became ready -- NOT RUN"; overall=1; continue; fi
  granted=""
  for _ in $(seq 1 30); do
    granted=$(docker exec "$engine" sh -c "$cli" "$TEST_PASSWORD" -e "
      GRANT ALL PRIVILEGES ON *.* TO '$TEST_USER'@'%' WITH GRANT OPTION;
      FLUSH PRIVILEGES;
      SET GLOBAL max_connections = 2000;
      SELECT @@lower_case_table_names;" 2>/dev/null | tail -1)
    [ -n "$granted" ] && break
    sleep 2
  done
  if [ -z "$granted" ]; then echo "  grant failed -- NOT RUN"; overall=1; continue; fi
  if [ ${#args[@]} -gt 0 ] && [ "$granted" != "1" ]; then
    echo "  the server reports lower_case_table_names=$granted, not 1 -- NOT RUN"; overall=1; continue
  fi

  # MSYS_NO_PATHCONV keeps Git Bash from rewriting the container-side paths; pwd -W gives Docker a Windows path.
  host_work=$(cd "$work" && (pwd -W 2>/dev/null || pwd))
  host_out=$(cd "$out" && (pwd -W 2>/dev/null || pwd))
  MSYS_NO_PATHCONV=1 docker run --rm --memory 6g --memory-swap 6g --network "container:$engine" "${envs[@]}" \
    -v "$host_work:/in:ro" -v "$host_out:/out" -v "$NUGET_VOLUME:/root/.nuget/packages" \
    "$SDK_IMAGE" bash /in/runner.sh "$category"
  rc=$?
  echo "  LEG $leg: $([ $rc -eq 0 ] && echo PASS || echo "FAIL (exit $rc)")"
  [ $rc -eq 0 ] || overall=1

  docker rm -fv "$engine" >/dev/null 2>&1
done

echo "Logs: $results"
exit $overall

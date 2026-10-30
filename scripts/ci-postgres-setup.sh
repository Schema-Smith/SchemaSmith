#!/usr/bin/env bash
# Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
# Raises a PostgreSQL container's max_connections to 2000 and fails unless the server reports it.
#
#   TEST_USER=... TEST_PASSWORD=... bash scripts/ci-postgres-setup.sh <container> <host> <port>
#
# CI's PostgreSQL job and scripts/run-floor-sweep.sh both run this file. psql is the runner's own; a host without one
# sets CI_PSQL_IMAGE and psql runs from that image instead, reaching <host> over TCP.
set -u
container=$1 host=$2 port=$3
: "${TEST_USER:?}" "${TEST_PASSWORD:?}"

pg() {
  if [ -n "${CI_PSQL_IMAGE:-}" ]; then
    docker run --rm --add-host host.docker.internal:host-gateway -e PGPASSWORD="$TEST_PASSWORD" "$CI_PSQL_IMAGE" \
      psql -h "$host" -p "$port" -U "$TEST_USER" -d TestMain "$@"
  else
    PGPASSWORD="$TEST_PASSWORD" psql -h "$host" -p "$port" -U "$TEST_USER" -d TestMain "$@"
  fi
}

# max_connections is a postmaster-level setting: ALTER SYSTEM takes effect only after a restart, and a restart can
# race, leaving the default 100 and starving the parallel fan-out tests ("too many clients already"). Apply, verify,
# retry once, and fail here rather than mid-suite.
apply_and_wait() {
  pg -c "ALTER SYSTEM SET max_connections = 2000;" >/dev/null
  docker restart "$container" >/dev/null
  sleep 5
  for _ in $(seq 1 30); do
    pg -c "SELECT 1" >/dev/null 2>&1 && break
    sleep 2
  done
}
get_max_conn() { pg -tAc "SHOW max_connections" 2>/dev/null | tr -d '[:space:]'; }

for _ in $(seq 1 30); do
  pg -c "SELECT 1" >/dev/null 2>&1 && break
  sleep 2
done
apply_and_wait
actual="$(get_max_conn)"
if [ "$actual" != "2000" ]; then
  echo "max_connections=$actual after first apply; retrying"
  apply_and_wait
  actual="$(get_max_conn)"
fi
echo "PostgreSQL max_connections=$actual"
[ "$actual" = "2000" ] || { echo "::error::Could not raise PostgreSQL max_connections to 2000 (got '$actual')."; exit 1; }

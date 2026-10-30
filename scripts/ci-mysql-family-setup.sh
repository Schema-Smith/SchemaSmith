#!/usr/bin/env bash
# Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
# Readies a MySQL-family server for the integration suite: waits for it over TCP, grants TestUser everything, raises
# max_connections, and with `lctn1` refuses a server not running lower_case_table_names=1.
#
#   TEST_USER=... TEST_PASSWORD=... bash scripts/ci-mysql-family-setup.sh <host> <port> <image>:<tag> [lctn1]
#
# CI's MySQL, MariaDB and lctn1 jobs run it with the runner's own mysql client (MySQL 8.0, OpenSSL 3); the local sweeps
# run the same file through a mysql:8.0 client container over TCP, so the step they check is the step CI runs. A copy
# inside the server container with its own client is not that step: it passed while CI's grant failed on MySQL 5.7.22.
set -u
host=$1 port=$2 image=$3 expect=${4:-}
: "${TEST_USER:?}" "${TEST_PASSWORD:?}"

# MySQL 5.7.22 offers only TLS 1.0/1.1, which an OpenSSL 3 client refuses; the tests run with SslMode=None there too.
ssl=()
case "$image" in mysql:5.7*) ssl=(--ssl-mode=DISABLED) ;; esac
q() { mysql -h "$host" -P "$port" -u root -p"$TEST_PASSWORD" "${ssl[@]}" -N "$@"; }

# Over TCP, so the entrypoint's temporary init server (no networking) cannot answer for the real one.
for _ in $(seq 1 60); do
  q -e "SELECT 1" >/dev/null 2>&1 && break
  sleep 3
done

out=""
for _ in $(seq 1 10); do
  out=$(q -e "
    GRANT ALL PRIVILEGES ON *.* TO '$TEST_USER'@'%' WITH GRANT OPTION;
    FLUSH PRIVILEGES;
    SET GLOBAL max_connections = 2000;
    SELECT @@version, @@lower_case_table_names;") && break
  out=""
  sleep 3
done
if [ -z "$out" ]; then
  echo "::error::Could not grant $TEST_USER on $image at $host:$port."
  q -e "SELECT 1" 2>&1 | tail -2
  exit 1
fi
echo "$out"

if [ "$expect" = lctn1 ] && ! printf '%s\n' "$out" | grep -qE '[[:space:]]1$'; then
  echo "::error::The server is not running with lower_case_table_names=1."
  exit 1
fi

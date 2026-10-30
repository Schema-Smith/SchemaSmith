# Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
# Sourced by the local sweeps: runs CI's own setup scripts (scripts/ci-*-setup.sh) the way CI's runner does, so the
# setup a local band gets is the setup a CI leg gets. Needs TEST_USER and TEST_PASSWORD.
#
# The runner's clients are ubuntu-latest's: MySQL 8.0 (built against OpenSSL 3) and PostgreSQL 16. A client inside
# the server container is not that client: MySQL 5.7.22's TLS 1.1 passed a grant there and failed CI's.
CI_MYSQL_CLIENT_IMAGE='mysql:8.0'
CI_PSQL_IMAGE='postgres:16'
export CI_PSQL_IMAGE

# <docker network: "bridge" or "container:<name>"> <host> <port> <image>:<tag> [lctn1]
# On the bridge the server is reached at its published port on host.docker.internal; inside a server's network
# namespace it is 127.0.0.1:3306.
ci_mysql_family_setup() {
  local net=$1; shift
  local scripts
  scripts=$(cd scripts && (pwd -W 2>/dev/null || pwd))
  local hostmap=()
  [ "$net" = bridge ] && hostmap=(--add-host host.docker.internal:host-gateway)
  MSYS_NO_PATHCONV=1 docker run --rm --network "$net" "${hostmap[@]}" \
    -e TEST_USER="$TEST_USER" -e TEST_PASSWORD="$TEST_PASSWORD" -v "$scripts:/ci:ro" \
    "$CI_MYSQL_CLIENT_IMAGE" bash /ci/ci-mysql-family-setup.sh "$@"
}

# <container> <published port>
ci_postgres_setup() {
  TEST_USER="$TEST_USER" TEST_PASSWORD="$TEST_PASSWORD" bash scripts/ci-postgres-setup.sh "$1" host.docker.internal "$2"
}

# <container> <sa password>
ci_sqlserver_setup() {
  bash scripts/ci-sqlserver-setup.sh "$1" "$2" "$TEST_USER"
}

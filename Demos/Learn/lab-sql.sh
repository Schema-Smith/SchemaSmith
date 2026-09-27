#!/usr/bin/env bash
# Shared connection helper for the Learn labs.
#
# Sandbox (default): runs each engine's client inside its container, exactly as the labs
# always have. Own server (LEARN_SERVER set by use-my-server.sh): runs the engine's
# client on your machine against your endpoint. The output is identical either way, so
# callers never care which path ran.
#
# Source it as a library:  . ../lab-sql.sh
# Or call it as a command: ../lab-sql.sh sqlserver ordersservice_prod "SELECT 1"

lab_own_server() { [ -n "${LEARN_SERVER:-}" ]; }

lab_engines() {
  if ! lab_own_server; then printf 'sqlserver\npostgres\nmysql\nmariadb\n'; return 0; fi
  if [ -z "${LEARN_ENGINE:-}" ]; then
    echo "LAB-SQL: LEARN_SERVER is set but LEARN_ENGINE is not. Re-run use-my-server.sh." >&2
    return 1
  fi
  printf '%s\n' "$LEARN_ENGINE"
}

lab_endpoint_label() {
  if lab_own_server; then echo "${LEARN_SERVER}:${LEARN_PORT}"; else echo "learn-$1 (container)"; fi
}

lab_client() {
  local client
  case "$1" in
    sqlserver) client=sqlcmd ;;
    postgres)  client=psql ;;
    mysql)     client=mysql ;;
    # MariaDB renamed its client to 'mariadb' in later releases; 10.2-10.4 ship only
    # 'mysql'. Accept either so a supported-but-older MariaDB isn't turned away.
    mariadb)   if command -v mariadb >/dev/null 2>&1; then client=mariadb; else client=mysql; fi ;;
  esac
  if ! command -v "$client" >/dev/null 2>&1; then
    echo "LAB-SQL: '$client' is required to reach your own $1 server but is not on PATH. Install the engine's command-line client (see docs/end-user/guide/use-your-own-server.md), re-open your shell, and verify with '$client --version'." >&2
    return 1
  fi
  printf '%s' "$client"
}

lab_container() {
  case "$1" in
    sqlserver) printf 'learn-sqlserver' ;;
    postgres)  printf 'learn-postgres' ;;
    mysql)     printf 'learn-mysql' ;;
    mariadb)   printf 'learn-mariadb' ;;
  esac
}

# MariaDB 10.2-10.4 containers have no 'mariadb' binary -- only 'mysql'. Probe the
# container so a floor-version sandbox is reachable.
lab_container_maria_client() {
  if docker exec "$1" sh -c 'command -v mariadb >/dev/null 2>&1' >/dev/null 2>&1; then
    printf 'mariadb'
  else
    printf 'mysql'
  fi
}

# The SQL Server 2017 image ships ONLY /opt/mssql-tools; 2019 and later ship /opt/mssql-tools18.
# Hardcoding the 18 path made every 2017 run fail the same way a server that never started does -- and
# that is not cosmetic: the compose healthcheck used the same path, so the container never went healthy,
# sqlserver-init never created the `learn` database, and every lab on the leg reported NOT-RUN.
#
# NOT CACHED, deliberately. The first version cached in a script-level variable, which was dead code: the
# call sites use $( ), that runs in a SUBSHELL, and the assignment never reached the parent -- so it probed
# every call anyway while reading as if it did not. Caching it properly would need keying by CONTAINER, and
# an unkeyed cache is actively wrong the moment two SQL Server containers are in play (probe 2017, cache the
# non-18 path, then talk to a 2022 container that ships only mssql-tools18 -> 'no such file', i.e. exactly
# the misdiagnosis this function exists to prevent). One probe per call is what lab_container_maria_client
# above already does, so this matches its sibling instead of inventing a third shape.
lab_container_sqlcmd() {
  if docker exec "$1" sh -c 'test -x /opt/mssql-tools18/bin/sqlcmd' >/dev/null 2>&1; then
    printf '%s' /opt/mssql-tools18/bin/sqlcmd
  else
    printf '%s' /opt/mssql-tools/bin/sqlcmd
  fi
}


# Trim leading and trailing whitespace so output matches the PowerShell twin exactly.
lab_trim() {
  local s="$1"
  s="${s#"${s%%[![:space:]]*}"}"
  s="${s%"${s##*[![:space:]]}"}"
  printf '%s' "$s"
}

# Keep stdout and stderr APART. Clients chatter on stderr even when they succeed -- MariaDB
# warns about "insecure passwordless login" on every connection -- and folding that into the
# result makes a query for a count return "WARNING...\n0" instead of "0". Callers compare
# results against exact values, so contaminated output silently takes the wrong branch. That
# is the same failure this helper exists to prevent.
lab_sql() {
  local engine="$1" db="$2" sql="$3" out err rc client container errfile
  errfile="$(mktemp)"
  if lab_own_server; then
    client="$(lab_client "$engine")" || { rm -f "$errfile"; return 1; }
    case "$engine" in
      sqlserver) out=$(sqlcmd -S "${LEARN_SERVER},${LEARN_PORT}" -U "$LEARN_USER" -P "$LEARN_PASSWORD" \
                         -C -b -d "$db" -h -1 -W -Q "SET NOCOUNT ON; $sql" 2>"$errfile"); rc=$? ;;
      postgres)  out=$(PGPASSWORD="$LEARN_PASSWORD" psql -h "$LEARN_SERVER" -p "$LEARN_PORT" -U "$LEARN_USER" \
                         -d "$db" -w -v ON_ERROR_STOP=1 --no-psqlrc -tAc "$sql" 2>"$errfile"); rc=$? ;;
      *)         out=$(MYSQL_PWD="$LEARN_PASSWORD" "$client" -h "$LEARN_SERVER" -P "$LEARN_PORT" \
                         -u "$LEARN_USER" -N -s -D "$db" -e "$sql" 2>"$errfile"); rc=$? ;;
    esac
  else
    container="$(lab_container "$engine")"
    case "$engine" in
      # MSYS_NO_PATHCONV keeps Git Bash from rewriting the container's /opt/... path;
      # it's harmless on Linux/macOS.
      sqlserver) sc=$(lab_container_sqlcmd "$container"); out=$(MSYS_NO_PATHCONV=1 docker exec "$container" "$sc" \
                         -S localhost -U sa -P 'Learn!Passw0rd' -C -b -d "$db" -h -1 -W \
                         -Q "SET NOCOUNT ON; $sql" 2>"$errfile"); rc=$? ;;
      postgres)  out=$(docker exec "$container" psql -U postgres -d "$db" -v ON_ERROR_STOP=1 -tAc "$sql" 2>"$errfile"); rc=$? ;;
      mysql)     out=$(docker exec -e 'MYSQL_PWD=Learn!Passw0rd' "$container" mysql -uroot -N -s -D "$db" -e "$sql" 2>"$errfile"); rc=$? ;;
      mariadb)   mc=$(lab_container_maria_client "$container"); out=$(docker exec -e 'MYSQL_PWD=Learn!Passw0rd' "$container" "$mc" -uroot -N -s -D "$db" -e "$sql" 2>"$errfile"); rc=$? ;;
    esac
  fi
  err="$(cat "$errfile" 2>/dev/null)"
  rm -f "$errfile"
  if [ "$rc" -ne 0 ]; then
    echo "LAB-SQL: could not reach $engine at $(lab_endpoint_label "$engine") [database '$db'] -- $(lab_trim "${err:-$out}")" >&2
    return 1
  fi
  lab_trim "$out"
}

# Git Bash hands scripts POSIX paths (/c/src/...), but the engine clients are native Windows
# programs: sqlcmd given /c/... reports "opening file C: Access is denied". cygpath exists
# only under MSYS/Cygwin, so on Linux and macOS this is a no-op and the path passes through.
lab_winpath() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

lab_sql_file() {
  local engine="$1" db="$2" path="$3" out rc client container clientpath
  if [ ! -f "$path" ]; then
    echo "LAB-SQL: no such SQL file '$path'." >&2
    return 1
  fi
  if lab_own_server; then
    # Local clients read the file directly -- nothing has to be copied into a container.
    client="$(lab_client "$engine")" || return 1
    path="$(lab_winpath "$path")"
    case "$engine" in
      sqlserver) out=$(sqlcmd -S "${LEARN_SERVER},${LEARN_PORT}" -U "$LEARN_USER" -P "$LEARN_PASSWORD" \
                         -C -b -d "$db" -i "$path" 2>&1); rc=$? ;;
      postgres)  out=$(PGPASSWORD="$LEARN_PASSWORD" psql -h "$LEARN_SERVER" -p "$LEARN_PORT" -U "$LEARN_USER" \
                         -d "$db" -w -v ON_ERROR_STOP=1 --no-psqlrc -f "$path" 2>&1); rc=$? ;;
      *)         out=$(MYSQL_PWD="$LEARN_PASSWORD" "$client" -h "$LEARN_SERVER" -P "$LEARN_PORT" \
                         -u "$LEARN_USER" -D "$db" -e "source $path" 2>&1); rc=$? ;;
    esac
  else
    container="$(lab_container "$engine")"
    clientpath="$(lab_winpath "$path")"
    if ! MSYS_NO_PATHCONV=1 docker cp "$clientpath" "${container}:/tmp/lab-seed.sql" >/dev/null 2>&1; then
      echo "LAB-SQL: could not stage '$(basename "$path")' into $container." >&2
      return 1
    fi
    case "$engine" in
      sqlserver) sc=$(lab_container_sqlcmd "$container"); out=$(MSYS_NO_PATHCONV=1 docker exec "$container" "$sc" \
                         -S localhost -U sa -P 'Learn!Passw0rd' -C -b -d "$db" -i /tmp/lab-seed.sql 2>&1); rc=$? ;;
      postgres)  out=$(MSYS_NO_PATHCONV=1 docker exec "$container" psql -U postgres -d "$db" -v ON_ERROR_STOP=1 -f /tmp/lab-seed.sql 2>&1); rc=$? ;;
      # The seed file is fed on STDIN, not via -e 'source ...'. `source` is a mysql CLIENT command rather
      # than SQL: it worked on 8.0 and MySQL 26.7 rejects it outright (ERROR 1064 near 'source /tmp/lab-seed.sql'),
      # which broke four course4 recipes on the ceiling. Redirecting the file is plain SQL and behaves the
      # same on every supported version, so this is the portable form and not a 26-specific patch.
      mysql)     out=$(MSYS_NO_PATHCONV=1 docker exec -e 'MYSQL_PWD=Learn!Passw0rd' "$container" sh -c "mysql -uroot -D '$db' < /tmp/lab-seed.sql" 2>&1); rc=$? ;;
      mariadb)   mc=$(lab_container_maria_client "$container"); out=$(MSYS_NO_PATHCONV=1 docker exec -e 'MYSQL_PWD=Learn!Passw0rd' "$container" sh -c "$mc -uroot -D '$db' < /tmp/lab-seed.sql" 2>&1); rc=$? ;;
    esac
  fi
  if [ "$rc" -ne 0 ]; then
    echo "LAB-SQL: failed running '$(basename "$path")' against $engine at $(lab_endpoint_label "$engine") [database '$db'] -- $(lab_trim "$out")" >&2
    return 1
  fi
}

LAB_STAMP='SchemaSmith_DemoProvisioned'

lab_admin_db() {
  case "$1" in
    sqlserver) printf 'master' ;;
    postgres)  printf 'postgres' ;;
    *)         printf 'information_schema' ;;
  esac
}

lab_add_stamp() {
  local engine="$1" db="$2"
  case "$engine" in
    sqlserver) lab_sql "$engine" "$db" "IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 0 AND name = '$LAB_STAMP') EXEC sys.sp_addextendedproperty @name = N'$LAB_STAMP', @value = N'1'" >/dev/null ;;
    postgres)  lab_sql "$engine" "$db" "COMMENT ON DATABASE \"$db\" IS '$LAB_STAMP'" >/dev/null ;;
    *)         lab_sql "$engine" "$db" "CREATE TABLE IF NOT EXISTS \`$LAB_STAMP\` (marker TINYINT NOT NULL)" >/dev/null ;;
  esac
}

# Creates the database if it's absent and stamps it as lab-provisioned. An existing
# database that carries the stamp is reused. An existing database WITHOUT the stamp is
# adopted in the sandbox (the container is a throwaway we created, and sandboxes built
# before this helper carry no stamp) but REFUSED on your own server, where it is far
# more likely to be a real database of yours that a deploy would damage.
lab_confirm_db() {
  local engine="$1" db="$2" admin exists_sql create_sql stamped_sql
  admin="$(lab_admin_db "$engine")"

  case "$engine" in
    sqlserver) exists_sql="SELECT COUNT(*) FROM sys.databases WHERE name = '$db'" ;;
    postgres)  exists_sql="SELECT COUNT(*) FROM pg_database WHERE datname = '$db'" ;;
    *)         exists_sql="SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = '$db'" ;;
  esac
  if [ "$(lab_sql "$engine" "$admin" "$exists_sql")" = "0" ]; then
    # Take the server's default character set and collation. Forcing one here (the demo
    # helper pins utf8mb4_unicode_ci) leaves lab databases collated differently from
    # everything else on the server -- MySQL 8 defaults to utf8mb4_0900_ai_ci and MariaDB
    # 11.4 to utf8mb4_uca1400_ai_ci -- and any later comparison across the two fails with
    # "Illegal mix of collations". The labs have always used the server default.
    case "$engine" in
      sqlserver) create_sql="CREATE DATABASE [$db]" ;;
      postgres)  create_sql="CREATE DATABASE \"$db\"" ;;
      *)         create_sql="CREATE DATABASE \`$db\`" ;;
    esac
    lab_sql "$engine" "$admin" "$create_sql" >/dev/null || return 1
    lab_add_stamp "$engine" "$db" || return 1
    printf 'created'
    return 0
  fi

  case "$engine" in
    sqlserver) stamped_sql="SELECT COUNT(*) FROM sys.extended_properties WHERE class = 0 AND name = '$LAB_STAMP'" ;;
    postgres)  stamped_sql="SELECT COUNT(*) FROM pg_database WHERE datname = current_database() AND COALESCE(shobj_description(oid, 'pg_database'), '') = '$LAB_STAMP'" ;;
    *)         stamped_sql="SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '$db' AND table_name = '$LAB_STAMP'" ;;
  esac
  if [ "$(lab_sql "$engine" "$db" "$stamped_sql")" != "0" ]; then printf 'reused'; return 0; fi

  if lab_own_server; then
    echo "LAB-SQL: a database named '$db' already exists on $(lab_endpoint_label "$engine") but was NOT created by the labs." >&2
    echo "The labs will not touch a database they didn't create -- deploying into it could drop columns." >&2
    echo "Rename or move your '$db', then re-run this setup script." >&2
    return 1
  fi
  lab_add_stamp "$engine" "$db" || return 1
  printf 'reused'
}

# Drops a database so a course can start from a clean slate. Mirrors lab_confirm_db's
# ownership rule: on your own server only a database the labs stamped is ever dropped, so a
# real database of yours that shares a lab name is refused, never destroyed. In the sandbox
# the container is a throwaway we created, so an unstamped database is dropped too.
# Prints 'dropped', 'absent', or 'refused'.
lab_remove_db() {
  local engine="$1" db="$2" admin exists_sql stamped_sql drop_sql
  admin="$(lab_admin_db "$engine")"

  case "$engine" in
    sqlserver) exists_sql="SELECT COUNT(*) FROM sys.databases WHERE name = '$db'" ;;
    postgres)  exists_sql="SELECT COUNT(*) FROM pg_database WHERE datname = '$db'" ;;
    *)         exists_sql="SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = '$db'" ;;
  esac
  if [ "$(lab_sql "$engine" "$admin" "$exists_sql")" = "0" ]; then printf 'absent'; return 0; fi

  if lab_own_server; then
    case "$engine" in
      sqlserver) stamped_sql="SELECT COUNT(*) FROM sys.extended_properties WHERE class = 0 AND name = '$LAB_STAMP'" ;;
      postgres)  stamped_sql="SELECT COUNT(*) FROM pg_database WHERE datname = current_database() AND COALESCE(shobj_description(oid, 'pg_database'), '') = '$LAB_STAMP'" ;;
      *)         stamped_sql="SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '$db' AND table_name = '$LAB_STAMP'" ;;
    esac
    if [ "$(lab_sql "$engine" "$db" "$stamped_sql")" = "0" ]; then printf 'refused'; return 0; fi
  fi

  # PostgreSQL: evict sessions and drop as SEPARATE statements. psql -c wraps its whole
  # string in one transaction, and DROP DATABASE cannot run inside a transaction block.
  if [ "$engine" = "postgres" ]; then
    lab_sql "$engine" "$admin" "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$db' AND pid <> pg_backend_pid()" >/dev/null || return 1
    lab_sql "$engine" "$admin" "DROP DATABASE IF EXISTS \"$db\"" >/dev/null || return 1
    printf 'dropped'
    return 0
  fi

  case "$engine" in
    sqlserver) drop_sql="ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db];" ;;
    *)         drop_sql="DROP DATABASE IF EXISTS \`$db\`" ;;
  esac
  lab_sql "$engine" "$admin" "$drop_sql" >/dev/null || return 1
  printf 'dropped'
}

# Called as a command rather than sourced:
#   lab-sql.sh <engine> <database> "<sql>"
#   lab-sql.sh <engine> <database> --file <path.sql>
if [ "${BASH_SOURCE[0]}" = "$0" ]; then
  if [ "$#" -lt 3 ]; then
    echo "usage: lab-sql.sh <engine> <database> \"<sql>\"" >&2
    echo "       lab-sql.sh <engine> <database> --file <path.sql>" >&2
    exit 2
  fi
  if [ "$3" = "--file" ]; then
    if [ "$#" -lt 4 ]; then
      echo "LAB-SQL: --file needs a path. Usage: lab-sql.sh <engine> <database> --file <path.sql>" >&2
      exit 2
    fi
    lab_sql_file "$1" "$2" "$4"
  else
    lab_sql "$1" "$2" "$3"
  fi
fi

#!/usr/bin/env bash
# Upgrade learn_upgrade the way a server upgrade moves it: SQL Server raises the database's compatibility
# level in place; PostgreSQL, MySQL and MariaDB dump it from the floor container and reload it into the
# current one, with the engines' default dump settings.
set -eu
engine="${1:?usage: upgrade.sh <sqlserver|postgres|mysql|mariadb>}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$here/../lab-sql.sh"
pw='Learn!Passw0rd'

case "$engine" in
  sqlserver)
    lab_sql sqlserver master "ALTER DATABASE learn_upgrade SET COMPATIBILITY_LEVEL = 160" >/dev/null
    echo "learn_upgrade: compatibility level 100 -> 160" ;;
  postgres)
    lab_sql postgres postgres "DROP DATABASE IF EXISTS learn_upgrade" >/dev/null
    lab_sql postgres postgres "CREATE DATABASE learn_upgrade" >/dev/null
    docker exec learn-postgres-12 pg_dump -U postgres learn_upgrade \
      | docker exec -i learn-postgres psql -q -U postgres -d learn_upgrade >/dev/null
    echo "learn_upgrade: PostgreSQL 12 -> 16 (pg_dump, then psql)" ;;
  mysql)
    lab_sql mysql information_schema "DROP DATABASE IF EXISTS learn_upgrade" >/dev/null
    lab_sql mysql information_schema "CREATE DATABASE learn_upgrade" >/dev/null
    docker exec -e MYSQL_PWD="$pw" learn-mysql-57 mysqldump -uroot learn_upgrade 2>/dev/null \
      | docker exec -i -e MYSQL_PWD="$pw" learn-mysql mysql -uroot learn_upgrade
    echo "learn_upgrade: MySQL 5.7 -> 8.0 (mysqldump, then mysql)" ;;
  mariadb)
    lab_sql mariadb information_schema "DROP DATABASE IF EXISTS learn_upgrade" >/dev/null
    lab_sql mariadb information_schema "CREATE DATABASE learn_upgrade" >/dev/null
    docker exec -e MYSQL_PWD="$pw" learn-mariadb-102 mysqldump -uroot learn_upgrade 2>/dev/null \
      | docker exec -i -e MYSQL_PWD="$pw" learn-mariadb mariadb -uroot learn_upgrade
    echo "learn_upgrade: MariaDB 10.2 -> 11.4 (mysqldump, then mariadb)" ;;
  *) echo "upgrade.sh: unknown engine '$engine'" >&2; exit 1 ;;
esac

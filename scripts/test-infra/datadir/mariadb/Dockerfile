# MariaDB with a pre-created, mysql-owned directory for InnoDB DATA DIRECTORY placement (F2c),
# for LOCAL-ONLY DataDirectory integration tests. CREATE TABLE ... DATA DIRECTORY='/ddspace' needs
# /ddspace to already exist and be writable by the mysqld process -- the stock demo/CI container has
# no such directory, and tests cannot docker exec to create one, so this is a purpose-built image run
# via scripts/run-datadir-sweep.sh, the same pattern as the encryption sweep.
#
# No server config change needed here -- unlike the MySQL image alongside this one, MariaDB requires no
# innodb_directories allow-list; DATA DIRECTORY works against any writable path a stock server can reach.
ARG MARIADB_TAG=11.4
FROM mariadb:${MARIADB_TAG}
RUN mkdir -p /ddspace && chown mysql:mysql /ddspace && chmod 750 /ddspace

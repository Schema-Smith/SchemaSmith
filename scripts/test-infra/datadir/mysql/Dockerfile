# MySQL with innodb_directories pointed at a pre-created, mysql-owned directory, for LOCAL-ONLY
# DataDirectory integration tests (F2c). Verified live 2026-09-04: CREATE TABLE ... DATA DIRECTORY='/x'
# fails with ERROR 3121 ("must be in a known directory") unless the server was started with
# --innodb_directories listing that path -- this is genuine server configuration a user must set, not
# something SchemaSmith can work around, so the test image supplies it the same way a real deployment
# target would.
#
# Config drop-in (/etc/mysql/conf.d/*.cnf), not a CMD default arg: the official image's own my.cnf
# already `!includedir`s that directory, so this is the same mechanism the sibling MariaDB encryption
# image (scripts/test-infra/encryption/mariadb) uses for its plugin config -- one COPY, no ENTRYPOINT
# override to maintain.
ARG MYSQL_TAG=8.0
FROM mysql:${MYSQL_TAG}
COPY datadir.cnf /etc/mysql/conf.d/datadir.cnf
RUN mkdir -p /ddspace && chown mysql:mysql /ddspace && chmod 750 /ddspace

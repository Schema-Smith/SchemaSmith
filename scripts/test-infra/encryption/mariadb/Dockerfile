# MariaDB with at-rest encryption enabled, for LOCAL-ONLY encryption integration tests.
# TEST-ONLY key -- not a secret. CI cannot run this (Actions services can't mount plugin config),
# so encryption tests run via scripts/run-encryption-sweep.sh, mirroring the genuine-binary sweep.
ARG MARIADB_TAG=11.4
FROM mariadb:${MARIADB_TAG}
COPY keyfile.txt /etc/mysql/keyfile.txt
COPY encryption.cnf /etc/mysql/conf.d/encryption.cnf
# The key file must be read-only to the mysql user mariadbd runs as, or file_key_management
# aborts InnoDB startup ("Permission denied") -- verified 2026-09-04.
RUN chown mysql:mysql /etc/mysql/keyfile.txt && chmod 0400 /etc/mysql/keyfile.txt

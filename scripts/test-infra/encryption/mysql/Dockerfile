# MySQL 8.0 keyring component setup for at-rest encryption -- CURRENTLY BLOCKED. See README.md.
# The manifest + config are placed per the MySQL manual (config in @@plugin_dir, /usr/lib64/mysql/plugin),
# read-only + mysql-owned, but component_keyring_file will not initialize inside the Oracle image's
# entrypoint (Component_status=Disabled; consistent with MySQL bug #108197 family). MySQL encryption
# integration tests are therefore [Explicit]/skipped until a custom-entrypoint workaround lands.
ARG MYSQL_TAG=8.0
FROM mysql:${MYSQL_TAG}
COPY mysqld.my /usr/sbin/mysqld.my
COPY component_keyring_file.cnf /usr/lib64/mysql/plugin/component_keyring_file.cnf
RUN chown mysql:mysql /usr/sbin/mysqld.my /usr/lib64/mysql/plugin/component_keyring_file.cnf \
 && chmod 0400 /usr/sbin/mysqld.my /usr/lib64/mysql/plugin/component_keyring_file.cnf \
 && mkdir -p /var/lib/mysql-keyring && chown mysql:mysql /var/lib/mysql-keyring && chmod 0750 /var/lib/mysql-keyring

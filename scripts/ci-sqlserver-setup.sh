#!/usr/bin/env bash
# Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
# Renames a SQL Server container's sa login to the test user, as the integration suite expects.
#
#   bash scripts/ci-sqlserver-setup.sh <container> <password> <test-user>
#
# CI's SQL Server job and scripts/run-modern-band-sweep.sh both run this file, so a local band logs in the way CI does.
# The sqlcmd path stays inside an sh -c string: from Git Bash a bare /opt/... argument is rewritten into a Windows path
# before docker sees it.
set -u
container=$1 password=$2 user=$3

sqlcmd=$(docker exec "$container" sh -c 'ls /opt/mssql-tools18/bin/sqlcmd /opt/mssql-tools/bin/sqlcmd 2>/dev/null | head -1')
# tools18 requires -C to trust the self-signed certificate; the older tools predate the flag.
case "$sqlcmd" in *tools18*) tls=-C ;; *) tls= ;; esac
[ -n "$sqlcmd" ] || { echo "::error::No sqlcmd in $container."; exit 1; }

docker exec "$container" sh -c "$sqlcmd -S localhost -U sa -P '$password' $tls -b -Q \"ALTER LOGIN [sa] WITH NAME = [$user];\""

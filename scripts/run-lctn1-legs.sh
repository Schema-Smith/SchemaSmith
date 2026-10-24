#!/usr/bin/env bash
# Paths below are repo-relative; this anchors the script so it runs from anywhere.
cd "$(dirname "$0")/.." || exit 1
# lower_case_table_names=1 legs (pre-PR gate). LOCAL-ONLY convenience mirroring CI's integration-tests-lctn1 job:
# MySQL 8.0 runs on every pull request, MariaDB 11.4 on merge to main, and both run here.
#
# WHY THIS EXISTS. Every other leg, local and CI, runs the Linux default lower_case_table_names=0, so a name
# compared by its declared spelling passes them all and fails only here: the server stores database and table
# names in lowercase. PR #583 shipped a silently skipped index change this way, caught only by CI.
#
# The server argument is the point, so the check that it took is not optional: a container that ignored it
# would run a green leg that tested nothing this leg exists for.
#
# WHERE THE TESTS RUN. On Linux, in a .NET SDK container, as CI runs them: scripts/run-linux-leg.sh's lctn1 legs.
# This script used to run the tests on the Windows host against a Linux container, and that passed tests CI failed:
# a checkpoint named after the database's declared spelling was found on NTFS, which ignores case, and not on CI's
# file system, where the deploy looks for the server's lowercase name (PR #592). Like that script, it tests the
# committed HEAD.
cd "$(dirname "$0")/.." || exit 1
exec bash scripts/run-linux-leg.sh mysql80lctn1 mariadb114lctn1

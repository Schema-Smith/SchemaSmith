#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$SCRIPT_DIR/../../.."

. "$REPO_ROOT/Demos/schemaquench-image.sh" "$@"

echo "Starting SQL Server compatibility-level demo..."
cd "$SCRIPT_DIR"

# `up` (foreground) lets the verify service's output reach the console.
# verify blocks on the quench completing, so both databases are deployed by then.
docker compose up --build --abort-on-container-exit verify

#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$SCRIPT_DIR/../../.."

. "$REPO_ROOT/Demos/schemaquench-image.sh" "$@"

echo "Starting PostgreSQL conditional-deployment demo..."
cd "$SCRIPT_DIR"

# `up` (foreground) lets the verify service's output reach the console.
# The verify service blocks on both quench services completing, so by the
# time the verify output prints, both deploys are done.
docker compose up --build --abort-on-container-exit verify

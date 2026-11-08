#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$SCRIPT_DIR/../.."

. "$REPO_ROOT/Demos/schemaquench-image.sh" "$@"

echo "Starting SQL Server demo..."
cd "$SCRIPT_DIR"

# `up -d` blocks on the service_completed_successfully chain, so by the time it
# returns every quench has finished. Don't follow up with `docker compose wait
# completed` — it races with the completed service's cleanup and Docker Desktop
# emits "no containers for project" when the container is already gone, which
# trips `set -e` even though provisioning succeeded.
docker compose up --build -d

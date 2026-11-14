# Sourced by every run-demo.sh, with REPO_ROOT set and the launcher's own arguments.
# Picks the SchemaQuench image the demo's quench services run:
#   default        the published release image (ghcr.io/schema-smith/schemaquench:latest), pulled fresh
#   --from-source  publish SchemaQuench from this checkout and build a local image (needs the .NET SDK)
# Set SCHEMAQUENCH_IMAGE yourself to pin a release, e.g. ghcr.io/schema-smith/schemaquench:2.8.0.

if [ "${1:-}" = "--from-source" ]; then
  echo "Publishing SchemaQuench from source..."
  "$REPO_ROOT/build-schemaquench.sh"
  docker build -q -t schemaquench:source -f "$REPO_ROOT/SchemaQuench/Dockerfile.release" "$REPO_ROOT/SchemaQuench" >/dev/null
  export SCHEMAQUENCH_IMAGE=schemaquench:source
else
  export SCHEMAQUENCH_IMAGE="${SCHEMAQUENCH_IMAGE:-ghcr.io/schema-smith/schemaquench:latest}"
  echo "Pulling $SCHEMAQUENCH_IMAGE..."
  # A pull failure is fine when the image is already local (offline, or a locally built tag).
  docker pull -q "$SCHEMAQUENCH_IMAGE" >/dev/null || docker image inspect "$SCHEMAQUENCH_IMAGE" >/dev/null
fi

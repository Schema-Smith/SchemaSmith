#!/usr/bin/env bash
# SchemaQuench Docker publish — entrypoint + resolution library.
# Sourced with --lib-only by unit tests; run directly by distribution-publish.yml's docker job.
# GHCR is always published (GITHUB_TOKEN); Docker Hub only when SS_PUSH_HUB=true.
set -uo pipefail

REPO="Schema-Smith/SchemaSmith"
GHCR_REPO="ghcr.io/schema-smith/schemaquench"
HUB_REPO="schemasmithyfree/schemaquench"

# derive_tags <version> -> newline list: full (YYYY.MM.N), year.month (that month's latest hotfix), latest.
# No bare-year tag: under dated versions it would move every month, so there is nothing to pin to.
derive_tags() {
  local v="$1"
  printf '%s\n%s\nlatest\n' "$v" "${v%.*}"
}

# image_refs <repo> <version> -> "<repo>:<tag>" one per line
image_refs() {
  local repo="$1" version="$2" tag
  while IFS= read -r tag; do printf '%s:%s\n' "$repo" "$tag"; done < <(derive_tags "$version")
}

# collect_tag_args -> "-t ref -t ref ..." across active registries.
# Reads SS_VERSION (required) and SS_PUSH_HUB (true|false). GHCR always; Hub if enabled.
collect_tag_args() {
  local ref args=()
  while IFS= read -r ref; do args+=(-t "$ref"); done < <(image_refs "$GHCR_REPO" "$SS_VERSION")
  if [ "${SS_PUSH_HUB:-false}" = "true" ]; then
    while IFS= read -r ref; do args+=(-t "$ref"); done < <(image_refs "$HUB_REPO" "$SS_VERSION")
  fi
  printf '%s ' "${args[@]}"
}

# stage_arch <arch> <dir> : copy SchemaQuench + its ICU libs out of an extracted release
# tarball (flat layout) into publish/linux/<arch>/ (the Dockerfile COPY source).
stage_arch() {
  local arch="$1" dl="$2" dest="publish/linux/$1" srcbin srcdir
  srcbin="$(find "$dl" -type f -name SchemaQuench | head -n1)"
  [ -n "$srcbin" ] || { echo "::error::SchemaQuench binary not found under $dl"; exit 1; }
  srcdir="$(dirname "$srcbin")"
  mkdir -p "$dest"
  cp "$srcbin" "$dest/"
  cp "$srcdir"/libicu*.so.* "$dest/"
  chmod 0755 "$dest/SchemaQuench"
  chmod 0644 "$dest"/libicu*.so.*
}

# download_stage <docker_arch> <rid> : fetch the published release's tarball for <rid>,
# extract it, and stage SchemaQuench + ICU into publish/linux/<docker_arch>/.
download_stage() {
  local arch="$1" rid="$2" tmp
  local tb="SchemaSmith-${SS_VERSION}-${rid}.tar.gz"
  tmp="$(mktemp -d)"
  gh release download "v${SS_VERSION}" --repo "$REPO" --pattern "$tb" --dir "$tmp"
  mkdir -p "$tmp/x"
  tar -xzf "$tmp/$tb" -C "$tmp/x"
  stage_arch "$arch" "$tmp/x"
}

# When sourced with --lib-only, expose functions and stop (no main run).
# NB: --lib-only returns BEFORE `set -e` below, so the test harness (which relies on
# non-zero exits from grep etc.) is unaffected; fail-fast applies only to a real publish.
if [ "${1:-}" = "--lib-only" ]; then return 0 2>/dev/null || exit 0; fi

set -e   # direct-execution (publish) mode: fail fast on login/build/push errors

main() {
  local version="$SS_VERSION"
  # Tag derivation assumes clean 3-part semver; a 4-part token would mistag (X.Y.Z.0 -> X.Y=X.Y.Z).
  [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "::error::SS_VERSION '$version' is not 3-part semver"; exit 1; }
  echo "Publishing SchemaQuench $version (push_hub=${SS_PUSH_HUB:-false})"

  # Registry auth (raw docker login — no third-party login action to pin).
  echo "$GHCR_TOKEN" | docker login ghcr.io -u "$GHCR_ACTOR" --password-stdin
  if [ "${SS_PUSH_HUB:-false}" = "true" ]; then
    echo "$DOCKERHUB_TOKEN" | docker login -u "$DOCKERHUB_USERNAME" --password-stdin
  fi

  # Stage the exact linux binaries from the PUBLISHED release (image == release, zero skew).
  download_stage amd64 linux-x64
  download_stage arm64 linux-arm64

  local label_args=(
    --label "org.opencontainers.image.source=https://github.com/$REPO"
    --label "org.opencontainers.image.description=SchemaQuench — state-based schema deployment (SQL Server, PostgreSQL, MySQL, MariaDB)"
    --label "org.opencontainers.image.licenses=LicenseRef-SSCL-2.0"
    --label "org.opencontainers.image.version=$version"
  )

  local tag_args; read -r -a tag_args <<< "$(collect_tag_args)"
  [ "${#tag_args[@]}" -gt 0 ] || { echo "::error::no push targets resolved"; exit 1; }
  echo "buildx tags: ${tag_args[*]}"

  # The multi-platform builder (docker-container driver) is provided by the workflow's
  # docker/setup-buildx-action step; QEMU by docker/setup-qemu-action.
  docker buildx build --platform linux/amd64,linux/arm64 --provenance=false \
    -f packaging/docker/Dockerfile "${label_args[@]}" "${tag_args[@]}" --push .

  # Confirm the multi-arch manifest resolved on the first tag (tag_args = [-t ref -t ref ...]).
  docker buildx imagetools inspect "${tag_args[1]}"
}

main "$@"

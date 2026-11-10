#!/usr/bin/env bash
# Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
#
# trusted-shas.sh <release-sha> [limit]
#
# Prints the commits whose CI results still describe the release commit's product tree, one per line: the release
# commit itself, then every ancestor whose tree differs from it only in paths CI does not test (read from
# .github/ci-tested-paths.txt, the list CI's own trigger is copied from). Every parent is walked, not only the first:
# CI ran on a merged branch's head, which a merge commit reaches only through its second parent. The walk goes on
# through an ancestor only while it is product-identical, and stops after <limit> commits (default 50).
# Run from the repository root.
set -euo pipefail

RELEASE_SHA="$1"
LIMIT="${2:-50}"
HERE="$(cd "$(dirname "$0")" && pwd)"
FILTER="$HERE/../ci_path_filter.py"
LIST="$HERE/../../.github/ci-tested-paths.txt"
PYTHON="${PYTHON:-python3}"

# The release tree differs from <sha> only in paths CI does not test.
product_identical() {
  local changed
  changed="$(git diff --name-only "$1" "$RELEASE_SHA")"
  [ -z "$changed" ] && return 0
  ! printf '%s\n' "$changed" | "$PYTHON" "$FILTER" "$LIST" > /dev/null
}

echo "$RELEASE_SHA"
queue=("$RELEASE_SHA")
seen=" $RELEASE_SHA "
visited=0
while [ "${#queue[@]}" -gt 0 ] && [ "$visited" -lt "$LIMIT" ]; do
  current="${queue[0]}"
  queue=("${queue[@]:1}")
  visited=$((visited + 1))
  for parent in $(git rev-list --parents -n 1 "$current" | cut -d' ' -f2-); do
    case "$seen" in *" $parent "*) continue ;; esac
    seen="$seen$parent "
    if product_identical "$parent"; then
      echo "$parent"
      queue+=("$parent")
    fi
  done
done

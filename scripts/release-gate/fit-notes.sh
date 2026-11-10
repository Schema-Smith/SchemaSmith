#!/usr/bin/env bash
# Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
#
# fit-notes.sh <notes.md> <version> <owner/repo> [limit]
#
# Writes the release body to stdout. GitHub refuses a release body over 125,000 characters, so notes over <limit>
# (default 125000) keep every section but Fixed, whose entries become a count and a link to the CHANGELOG at the tag.
# Notes still over the limit fail (exit 1), so the release stops before anything is tagged.
set -euo pipefail

NOTES="$1"
VERSION="$2"
REPO="$3"
LIMIT="${4:-125000}"

length() { LC_ALL=C.UTF-8 wc -m < "$1" | tr -d ' '; }

if [ "$(length "$NOTES")" -le "$LIMIT" ]; then
  cat "$NOTES"
  exit 0
fi

short="$(mktemp)"
trap 'rm -f "$short"' EXIT
fixes="$(awk '/^### / { fixed = ($0 == "### Fixed") } fixed && /^- / { n++ } END { print n + 0 }' "$NOTES")"
awk -v fixes="$fixes" -v link="https://github.com/${REPO}/blob/v${VERSION}/CHANGELOG.md" '
  /^### / {
    fixed = ($0 == "### Fixed")
    print
    if (fixed) { print ""; print fixes " fixes, listed in the [CHANGELOG](" link ")." }
    next
  }
  !fixed { print }
' "$NOTES" > "$short"

if [ "$(length "$short")" -gt "$LIMIT" ]; then
  echo "::error::The release notes are $(length "$short") characters even without the Fixed entries, over GitHub's limit of $LIMIT. Shorten the CHANGELOG section before releasing." >&2
  exit 1
fi
echo "::warning::The release notes were over $LIMIT characters, so Fixed is a count of $fixes and a link to the CHANGELOG." >&2
cat "$short"

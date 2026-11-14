#!/usr/bin/env bash
# Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
#
# Tests for the release gate's helpers: which commits' CI results hold for the release commit (trusted-shas.sh), and the
# release body that fits GitHub's limit (fit-notes.sh). Runs in throwaway git repositories; touches nothing else.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
RELEASE="$(cd "$HERE/.." && pwd)"
fails=0

assert_eq() {
  if [ "$1" = "$2" ]; then echo "ok   - $3"; else echo "FAIL - $3"; echo "       expected: $2"; echo "       actual:   $1"; fails=$((fails + 1)); fi
}

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# --- trusted-shas.sh -------------------------------------------------------------------------------------------------
repo="$WORK/repo"
mkdir -p "$repo" && cd "$repo" || exit 1
git init -q -b main . && git config user.email t@example.com && git config user.name t && git config commit.gpgsign false
commit() { mkdir -p "$(dirname "$1")"; echo "$2" > "$1"; git add -A; git commit -q -m "$3"; git rev-parse HEAD; }

X=$(commit Schema/a.cs 1 "product")
M1=$(commit Schema/b.cs 1 "product on main")
git checkout -q -b feature
H=$(commit Schema/c.cs 1 "product on the branch: CI ran here")
D=$(commit docs/notes.md 1 "docs on the branch")
git checkout -q main
git merge -q --no-ff -m "merge the branch" feature
R=$(git rev-parse HEAD)

trusted() { bash "$RELEASE/trusted-shas.sh" "$1" | sort | paste -sd, -; }

# The merge's first parent (M1) lacks the branch's product change, so a first-parent walk stops at R and never reaches
# the branch head CI ran on. Its second parent (D) differs from R in nothing; D's parent H differs only in docs.
assert_eq "$(trusted "$R")" "$(printf '%s\n' "$R" "$D" "$H" | sort | paste -sd, -)" "a merge: every parent is walked, product-identical ancestors on the second side are trusted"

E=$(commit docs/more.md 1 "docs on main after the merge")
assert_eq "$(trusted "$E")" "$(printf '%s\n' "$E" "$R" "$D" "$H" | sort | paste -sd, -)" "a docs-only commit trusts what its product-identical ancestors trusted"

P=$(commit Schema/d.cs 1 "product after the merge")
assert_eq "$(trusted "$P")" "$P" "a product change trusts only itself"

# --- fit-notes.sh ----------------------------------------------------------------------------------------------------
notes="$WORK/notes.md"
{
  printf '### Added\n\n- **One thing.** Text.\n\n### Fixed\n\n'
  for i in $(seq 1 40); do printf -- '- **Fix %s.** A sentence long enough to make forty of them outweigh the link.\n' "$i"; done
} > "$notes"

bash "$RELEASE/fit-notes.sh" "$notes" 2026.12.0 Schema-Smith/SchemaSmith 100000 > "$WORK/out.md"
assert_eq "$(cat "$WORK/out.md")" "$(cat "$notes")" "notes under the limit are used as they are"

bash "$RELEASE/fit-notes.sh" "$notes" 2026.12.0 Schema-Smith/SchemaSmith 500 > "$WORK/out.md"
assert_eq "$(grep -c '^- \*\*' "$WORK/out.md")" "1" "over the limit: the Fixed bullets go"
assert_eq "$(grep -c '^- \*\*One thing' "$WORK/out.md")" "1" "over the limit: every other section stays"
assert_eq "$(grep -c '40 fixes' "$WORK/out.md")" "1" "over the limit: Fixed says how many"
assert_eq "$(grep -c 'https://github.com/Schema-Smith/SchemaSmith/blob/v2026.12.0/CHANGELOG.md' "$WORK/out.md")" "1" "over the limit: Fixed links the CHANGELOG at the tag"

bash "$RELEASE/fit-notes.sh" "$notes" 2026.12.0 Schema-Smith/SchemaSmith 50 > "$WORK/out.md" 2> "$WORK/err.txt"
assert_eq "$?" "1" "still over the limit without Fixed: fails, so the release stops before the tag"
assert_eq "$(grep -c 'limit' "$WORK/err.txt")" "1" "and says why"

echo "---"; [ "$fails" -eq 0 ] && echo "ALL PASS" || { echo "$fails FAILED"; exit 1; }

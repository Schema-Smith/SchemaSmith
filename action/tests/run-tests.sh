#!/usr/bin/env bash
# Zero-dependency unit tests for the SchemaSmith action resolution functions.
set -uo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=/dev/null
source "$DIR/../schemasmith-deploy.sh" --lib-only

fails=0
assert_eq() { # $1=actual $2=expected $3=label
  if [ "$1" = "$2" ]; then echo "ok   - $3"; else echo "FAIL - $3: got '$1' want '$2'"; fails=$((fails+1)); fi
}
assert_fail() { # $1=label ; reads command via "$@" from index 2
  local label="$1"; shift
  if "$@" >/dev/null 2>&1; then echo "FAIL - $label: expected non-zero"; fails=$((fails+1)); else echo "ok   - $label"; fi
}

# resolve_rid — all 6 valid combos + both reject paths
assert_eq "$(resolve_rid Linux X64)"     "linux-x64"   "rid linux x64"
assert_eq "$(resolve_rid Linux ARM64)"   "linux-arm64" "rid linux arm64"
assert_eq "$(resolve_rid Windows X64)"   "win-x64"     "rid win x64"
assert_eq "$(resolve_rid Windows ARM64)" "win-arm64"   "rid win arm64"
assert_eq "$(resolve_rid macOS X64)"     "osx-x64"     "rid osx x64"
assert_eq "$(resolve_rid macOS ARM64)"   "osx-arm64"   "rid osx arm64"
assert_fail "rid rejects unknown os"   resolve_rid Solaris X64
assert_fail "rid rejects unknown arch" resolve_rid Linux X86

# resolve_version
assert_eq "$(resolve_version 'v2.2.0' 'v9.9.9')" "v2.2.0" "explicit version wins"
assert_eq "$(resolve_version 'latest' 'v2.3.0')" "latest" "explicit latest"
assert_eq "$(resolve_version '' 'v2.3.0')"       "v2.3.0" "default uses version-tag action_ref"
assert_eq "$(resolve_version '' 'v2026.12.0')"   "v2026.12.0" "default uses a dated version-tag action_ref"
assert_eq "$(resolve_version '' 'main')"         "latest" "default falls back to latest"

# select_asset_url
REL="$(cat "$DIR/fixtures/release.json")"
assert_eq "$(select_asset_url "$REL" 'win-x64')"   "https://example/win-x64.zip"       "asset win-x64"
assert_eq "$(select_asset_url "$REL" 'osx-arm64')" "https://example/osx-arm64.tar.gz"  "asset osx-arm64"
assert_eq "$(select_asset_url "$REL" 'linux-arm64')" ""                                "asset missing -> empty"

# mode_switch — every branch + reject
assert_eq "$(mode_switch deploy)"          ""                  "mode deploy no switch"
assert_eq "$(mode_switch whatif)"          ""                  "mode whatif no switch"
assert_eq "$(mode_switch validate)"        "--Validate"        "mode validate"
assert_eq "$(mode_switch test-connection)" "--TestConnection"  "mode test-connection"
assert_eq "$(mode_switch preview-targets)" "--PreviewTargets"  "mode preview-targets"
assert_fail "mode rejects unknown" mode_switch frobnicate

# mode_switch drift
assert_eq "$(mode_switch drift)" "--Drift" "mode drift"

# workflow-command escaping: data escapes % CR LF; a property also : and ,
# The input is built outside the substitution: Git Bash strips a literal CR from the text of a $(...).
CRLF_INPUT=$'100% done\r\nnext'
assert_eq "$(escape_data "$CRLF_INPUT")" "100%25 done%0D%0Anext" "escape data"
assert_eq "$(escape_property 'a,b:c%')" "a%2Cb%3Ac%25" "escape property"

# workspace_relative: Windows and Linux runners, outside the workspace
assert_eq "$(workspace_relative 'D:\a\db\db\pkg\Templates\x.sql' 'D:\a\db\db')" "pkg/Templates/x.sql" "relative windows"
assert_eq "$(workspace_relative 'd:\A\db\db\pkg\x.sql' 'D:\a\db\db\')" "pkg/x.sql" "relative windows, case and trailing separator"
assert_eq "$(workspace_relative '/home/runner/work/db/db/pkg/x.sql' '/home/runner/work/db/db')" "pkg/x.sql" "relative linux"
assert_eq "$(workspace_relative '/home/runner/work/db/dbx/x.sql' '/home/runner/work/db/db')" "" "a sibling folder is not inside"
assert_eq "$(workspace_relative '/elsewhere/x.sql' '/home/runner/work/db/db')" "" "outside the workspace"

# annotations from a failed run
FAILED="$(annotations_from_summary "$DIR/fixtures/summary-failed.json" 'D:\a\billing-db\billing-db')"
assert_eq "$(printf '%s\n' "$FAILED" | sed -n 1p)" \
  "::error file=pkg/Templates/Main/Procedures/dbo.Get.sql,title=[sql01].[Billing]::Unable to quench 'D:\a\billing-db\billing-db\pkg\Templates\Main\Procedures\dbo.Get.sql': Incorrect syntax near 'x'." \
  "failure on a package file: error on that file, first line of the message"
assert_eq "$(printf '%s\n' "$FAILED" | sed -n 2p)" "::error title=[srv%2C1%3A2].[db]::100%25 of the batch failed: near ','" "failure: escaped title and message"
assert_eq "$(printf '%s\n' "$FAILED" | sed -n 3p)" "::error title=[sql01].[Other]::Unable to quench 'C:\elsewhere\dbo.X.sql': boom" "failure outside the workspace: no file"
assert_eq "$(printf '%s\n' "$FAILED" | wc -l | tr -d ' ')" "3" "one line per failure"

# warnings from a drift check: the drifted target, the downgrade, the refused change; not the clean target or the applied change
DRIFT="$(annotations_from_summary "$DIR/fixtures/summary-drift.json" '/home/runner/work/db/db')"
assert_eq "$(printf '%s\n' "$DRIFT" | grep -c '^::warning')" "3" "three warnings"
assert_eq "$(printf '%s\n' "$DRIFT" | grep -c '^::error')" "0" "no errors"
assert_eq "$(printf '%s\n' "$DRIFT" | grep -c 'Billing_1\] \[Schema: tenant_1\] has 2 change')" "1" "drifted target named"
assert_eq "$(printf '%s\n' "$DRIFT" | grep -c 'Billing_2')" "0" "clean target not named"
assert_eq "$(printf '%s\n' "$DRIFT" | grep -c 'public.ix_orders')" "1" "downgrade named"
assert_eq "$(printf '%s\n' "$DRIFT" | grep -c '\[dbo\].\[T\].\[Old\]')" "1" "refused change named"
assert_eq "$(printf '%s\n' "$DRIFT" | grep -c 'Gone')" "0" "applied change not named"

# a clean run: nothing, and no error from jq on the nulls
assert_eq "$(annotations_from_summary "$DIR/fixtures/summary-clean.json" '/w' 2>&1)" "" "clean run: no annotations"

# more than ten: ten errors and one notice with the rest
MANY="$(mktemp)"
jq '.failures = [range(12) | {scopeKey: "[s].[d\(.)]", error: "e\(.)"}]' "$DIR/fixtures/summary-clean.json" > "$MANY"
OUT="$(annotations_from_summary "$MANY" '/w')"
assert_eq "$(printf '%s\n' "$OUT" | grep -c '^::error')" "10" "at most ten errors"
assert_eq "$(printf '%s\n' "$OUT" | grep '^::notice')" "::notice::2 more failures are in the deployment summary." "notice counts the rest"
rm -f "$MANY"

# no jq: one notice, nothing else, success
assert_eq "$(PATH=/nonexistent annotations_from_summary "$DIR/fixtures/summary-failed.json" '/w' | grep -c '^::notice')" "1" "no jq: a notice"
PATH=/nonexistent annotations_from_summary "$DIR/fixtures/summary-failed.json" '/w' >/dev/null; assert_eq "$?" "0" "no jq: still succeeds"

# step summary: appended whole, or cut with a pointer past the limit
SRC="$(mktemp)"; DEST="$(mktemp)"
printf '# Deployment Summary\n- line\n' > "$SRC"
write_step_summary "$SRC" "$DEST"
assert_eq "$(cat "$DEST")" "$(printf '# Deployment Summary\n- line')" "step summary appended whole"
: > "$DEST"; head -c 5000 /dev/zero | tr '\0' 'x' > "$SRC"
write_step_summary "$SRC" "$DEST" 1000
assert_eq "$(head -c 1000 "$DEST" | tr -d 'x' | wc -c | tr -d ' ')" "0" "cut summary keeps its head"
assert_eq "$(grep -c 'full summary is in the log directory' "$DEST")" "1" "cut summary says where the rest is"
assert_eq "$([ "$(wc -c < "$DEST")" -lt 1200 ] && echo small)" "small" "cut summary is under the limit plus the pointer"
rm -f "$SRC" "$DEST"

# pull-request comment body: marker first, cut at the limit with the pointer
SRC="$(mktemp)"; printf '# Deployment Summary\n' > "$SRC"
BODY="$(pr_comment_body "$SRC" '<!-- schemasmith-deploy:deploy:pkg -->')"
assert_eq "$(printf '%s\n' "$BODY" | sed -n 1p)" "<!-- schemasmith-deploy:deploy:pkg -->" "comment starts with its marker"
assert_eq "$(printf '%s\n' "$BODY" | grep -c '# Deployment Summary')" "1" "comment carries the summary"
head -c 5000 /dev/zero | tr '\0' 'y' > "$SRC"
BODY="$(pr_comment_body "$SRC" '<!-- m -->' 1000)"
assert_eq "$([ "${#BODY}" -le 1000 ] && echo fits)" "fits" "long comment fits the limit"
assert_eq "$(printf '%s' "$BODY" | grep -c 'full summary is in the log directory')" "1" "long comment says where the rest is"
rm -f "$SRC"

# finding an earlier run's comment by its marker
PAGE='[{"id":1,"body":"a reviewer"},{"id":7,"body":"<!-- schemasmith-deploy:whatif:pkg -->\n# Summary"},{"id":9,"body":null}]'
assert_eq "$(printf '%s' "$PAGE" | comment_with_marker '<!-- schemasmith-deploy:whatif:pkg -->')" "7" "comment found by its marker"
assert_eq "$(printf '%s' "$PAGE" | comment_with_marker '<!-- schemasmith-deploy:deploy:pkg -->')" "" "another mode's marker is another comment"
assert_eq "$(printf '%s' '[]' | comment_with_marker '<!-- m -->')" "" "an empty page finds nothing"
assert_eq "$(grep -c 'page=\$page' "$DIR/../schemasmith-deploy.sh")" "1" "the lookup pages through the comments"

echo "---"; [ "$fails" -eq 0 ] && echo "ALL PASS" || { echo "$fails FAILED"; exit 1; }

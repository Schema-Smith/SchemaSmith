#!/usr/bin/env bash
# Paths below are repo-relative; this anchors the script so it runs from anywhere.
cd "$(dirname "$0")/.." || exit 1
# Regenerate every committed .json-schemas directory (DoD #9).
# Required after any domain-model or SchemaGenerator change.
set -u
TONGS="SchemaTongs/bin/Debug/net10.0/SchemaTongs.dll"
[ -f "$TONGS" ] || { echo "build SchemaTongs first: dotnet build SchemaTongs/SchemaTongs.csproj"; exit 1; }
ok=0; fail=0; skipped=0
while IFS= read -r d; do
  pkg="$(dirname "$d")"
  [ -f "$pkg/Product.json" ] || continue
  # Regenerate COMMITTED schema directories only -- the header's actual contract, now enforced rather
  # than assumed. Running --Validate locally GENERATES schemas into a package that has none, so stray
  # untracked .json-schemas directories accumulate in the working tree; `find` then hands them here and
  # a sweep fills them with files nobody committed. That is how a 101-file regen turned into 343
  # untracked files, and it wrote schemas into NoCommittedSchemas/ and NoCommittedSchemasClean/ --
  # two Validate fixtures whose entire contract is having NONE, which the fill silently destroyed.
  # Keying on "does git track a schema here" covers those two, any future fixture with the same
  # contract, and every stray directory, without a name blocklist that has to be kept in sync.
  git ls-files --error-unmatch -- "$d" >/dev/null 2>&1 || { skipped=$((skipped+1)); continue; }
  # Skip ONLY the two fixtures whose .json-schemas state is itself the thing under test: StaleSchema
  # must STAY stale (it is what SS-STALE-001 detects) and PartialCommittedSchemas must keep ONE
  # type's schema missing (--WriteSchemasOnly writes every type and would fill the gap).
  # Every other Validate fixture MUST be regenerated, including the Misnamed* ones -- their defect
  # is a bad property in the package JSON, not a stale schema, and SS-STALE-001 short-circuits the
  # JSON-schema pass, so a stale schema there masks the very finding the test asserts.
  # StaleSchema must STAY stale -- its staleness is what SS-STALE-001 detects.
  case "$pkg" in
    */Fixtures/Validate/StaleSchema/*) continue;;
  esac
  # Also adds each package file's $schema reference. The two skips above are what protects the
  # fixtures whose state IS the thing under test; every other package -- fixtures included -- should
  # look like a real package, and a real package carries the reference.
  if SmithySettings_Product__Path="$pkg" dotnet "$TONGS" --WriteSchemasOnly >/dev/null 2>&1; then
    # PartialCommittedSchemas IS regenerated -- a stale schema there fires SS-STALE-001, which
    # short-circuits the JSON-schema pass its test asserts on -- but --WriteSchemasOnly writes EVERY
    # type, filling the one-type gap that is the fixture's actual subject. Re-open the gap after.
    # The $schema reference has to come out with it. --WriteSchemasOnly writes the tables schema and
    # then stamps against it, so by the time the gap is re-opened the table JSON already points at
    # the file about to be deleted -- a dangling reference an editor reports as a broken schema.
    case "$pkg" in
      */Fixtures/Validate/PartialCommittedSchemas/*)
        rm -f "$pkg"/.json-schemas/tables.*.schema
        find "$pkg" -path "*/Tables/*.json" -type f -exec sed -i '/"\$schema"[[:space:]]*:/d' {} +
        ;;
    esac
    ok=$((ok+1))
  else
    fail=$((fail+1)); echo "FAILED: $pkg"
  fi
done < <(find . -name ".json-schemas" -type d -not -path "*/bin/*" -not -path "*/obj/*")
echo "regenerated: $ok   skipped (no committed schemas): $skipped   failed: $fail"

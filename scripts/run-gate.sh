#!/usr/bin/env bash
# Paths below are repo-relative; this anchors the script so it runs from anywhere.
cd "$(dirname "$0")/.." || exit 1
# Full multi-engine green gate (CLAUDE.md Rule 25): unit + integration across all engines.
set -u
# Every run keeps a TRX per project, so a failure that does not reproduce alone still has its message and output.
results="TestResults/gate/$(date +%Y%m%d-%H%M%S)"
dotnet build SchemaSmith.sln -v q --nologo || exit 1
status=0
for p in Schema/Schema.UnitTests SchemaQuench/SchemaQuench.UnitTests SchemaTongs/SchemaTongs.UnitTests \
         DataTongs/DataTongs.UnitTests SchemaShears/SchemaShears.UnitTests \
         Schema/Schema.IntegrationTests DataTongs/DataTongs.IntegrationTests \
         SchemaTongs/SchemaTongs.IntegrationTests SchemaQuench/SchemaQuench.IntegrationTests; do
  n="$(basename "$p")"
  dotnet test "$p/$n.csproj" --no-build --results-directory "$results" --logger "trx;LogFileName=$n.trx" 2>&1 \
    | grep -E "^(Passed!|Failed!)|^  Failed "
  # The grep would otherwise decide the script's exit status, and a red run would read as green.
  [ "${PIPESTATUS[0]}" -eq 0 ] || status=1
done
echo "TRX results: $results"
exit $status

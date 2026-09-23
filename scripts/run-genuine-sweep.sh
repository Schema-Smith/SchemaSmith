#!/usr/bin/env bash
# Paths below are repo-relative; this anchors the script so it runs from anywhere.
cd "$(dirname "$0")/.." || exit 1
# Genuine-binary version sweep (pre-PR gate). LOCAL-ONLY: needs the four on-demand old SQL Server
# instances (C:\temp\sqlserver-oldbinaries\start-oldsql.ps1). CI has no genuine old binary.
#
# TWO DIFFERENT WIRINGS -- do not merge them:
#  1. Schema.IntegrationTests/GenuineOldBinary reads SmithySettings_SqlServer__* , so it must be
#     pointed at each old instance in turn. The folder now holds TWO fixture families with opposite
#     version predicates, so every instance both passes and skips some -- read the counts, not the
#     presence of skips:
#       * OldBinaryXml* prove the pre-2016 CREATE-time binding. They self-Ignore at 2016+, so they
#         run on 14330/14331/14332 and skip on 14333.
#       * Sql2016TemporalRetentionGateTests proves the 2016-only band where the JSON/STRING_AGG and
#         2017-catalog-column gates cross. It runs ONLY on major 13, so it skips on 14330/14331/14332
#         and runs on 14333. 14333 used to skip everything, which is exactly how two 2017-vs-2016
#         defects lived there unseen; an all-skip result on 14333 now means something is wrong.
#       * ExpressionChurnAcrossVersionsTests (#242) runs on EVERY instance: three tests measuring that the
#         expression surfaces stay put and that a real compatibility-level upgrade re-baselines. Its upgrade
#         test skips on 14330 only, because 2008 R2's default compatibility level is already 100.
#     Expected shape: 6 passed / 5 skipped on 14330, 7 passed / 4 skipped on 14331-14333.
#  2. SchemaQuench's GenuineSql2008EmitGuardCert HARDCODES 127.0.0.1,14330 for its own work, but it
#     lives in the SqlServer namespace whose [SetUpFixture] provisions Always Encrypted keys. Point
#     the env vars at an old instance and that setup dies with "SQL Server instance in use does not
#     support column encryption" -- which looks like three product failures and is not. Run it with
#     the DEFAULT settings so the SetUpFixture reaches the modern container.
set -u

# The sweep is local-only and manual, which is exactly how it gets skipped -- and skipping it, not a gap
# in what it covers, is how two 2017-vs-2016 defects reached the v2.5.0 cut. Every run writes a record so
# "was this run, against what, with what result" is answerable from the repository months later rather
# than from somebody's memory of a terminal.
RECORD="docs/development/genuine-sweep-results.md"
SWEEP_SHA="$(git rev-parse --short HEAD)"
SWEEP_STARTED="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
SWEEP_ROWS=""

# Port -> version, so the RECORD names what was certified rather than a number nobody can decode a
# year later. These four are the whole no-container range: MCR publishes no SQL Server image older
# than 2017, so majors 10 through 13 can ONLY be covered by a real local install. That is also why
# this sweep cannot move into CI -- it is not a preference, there is no image to run.
version_for() {
  case "$1" in
    14330) echo "2008 R2 (major 10)" ;;
    14331) echo "2012 (major 11)" ;;
    14332) echo "2014 (major 12)" ;;
    14333) echo "2016 (major 13)" ;;
    *)     echo "unknown instance" ;;
  esac
}

echo "===== 1. GenuineOldBinary, per instance ====="
for port in 14330 14331 14332 14333; do
  echo "--- instance $port -- SQL Server $(version_for "$port")"
  SmithySettings_SqlServer__Server=127.0.0.1 SmithySettings_SqlServer__Port=$port \
  SmithySettings_SqlServer__User=sa SmithySettings_SqlServer__Password='SchemaSmith!Old2026' \
  dotnet test Schema/Schema.IntegrationTests/Schema.IntegrationTests.csproj --no-build \
    --filter "FullyQualifiedName~GenuineOldBinary" 2>&1 | tee "/tmp/ss-sweep-$port.log" \
    | grep -E "^(Passed!|Failed!|No test)|^  (Failed|Skipped) "
  summary="$(grep -E "^(Passed!|Failed!)" "/tmp/ss-sweep-$port.log" | head -1 | sed "s/ - Duration.*//")"
  expected="7 passed / 4 skipped"; [ "$port" = "14330" ] && expected="6 passed / 5 skipped"
  SWEEP_ROWS="${SWEEP_ROWS}| SQL Server $(version_for "$port") @ $port | ${summary:-NO RESULT} | $expected |
"
done
echo "===== 2. 2008 emit-guard cert (default settings; fixture reaches 14330 itself) ====="
dotnet test SchemaQuench/SchemaQuench.IntegrationTests/SchemaQuench.IntegrationTests.csproj --no-build \
  --filter "FullyQualifiedName~GenuineSql2008EmitGuardCert" 2>&1 | grep -E "^(Passed!|Failed!)|^  Failed "

# Appended, never rewritten: the history of what was swept is the point.
mkdir -p "$(dirname "$RECORD")"
if [ ! -f "$RECORD" ]; then
  {
    echo "# Genuine-binary sweep results"
    echo ""
    echo "Appended by \`scripts/run-genuine-sweep.sh\`. CI cannot run this sweep -- no pre-2017 SQL"
    echo "Server Linux image exists -- so this file is the standing evidence that it ran, and against what."
  } > "$RECORD"
fi
{
  echo ""
  echo "## $SWEEP_STARTED - commit $SWEEP_SHA"
  echo ""
  echo "| Target | Result | Expected |"
  echo "|---|---|---|"
  printf "%s" "$SWEEP_ROWS"
} >> "$RECORD"
echo
echo "Recorded in $RECORD for commit $SWEEP_SHA -- commit it alongside the work it certifies."

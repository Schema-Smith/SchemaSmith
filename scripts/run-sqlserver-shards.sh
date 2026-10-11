#!/usr/bin/env bash
# Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
#
# Runs the SQL Server integration tests as three concurrent test processes against one server.
#
# Most SQL Server test time is in tests that run Program.Main, and those hold one in-process lock, so NUnit workers
# cannot overlap them. Separate processes can. Each runs from its own copy of the build output and with its own
# TEMP, because SchemaQuench writes its logs and deployment summary beside the binary and its default checkpoints
# under TEMP -- shared, three processes read each other's files. Databases are already unique per process.
#
# Usage: scripts/run-sqlserver-shards.sh [results-dir] [-- extra dotnet test arguments]
#   e.g. CI: scripts/run-sqlserver-shards.sh TestResults -- --settings coverage.runsettings --collect "XPlat Code Coverage"
# Expects a Debug build of the solution. Exits non-zero if any shard fails.
cd "$(dirname "$0")/.." || exit 1
set -u

results="${1:-TestResults/sqlserver-shards/$(date +%Y%m%d-%H%M%S)}"
shift || true
[ "${1:-}" = "--" ] && shift
extra=("$@")
mkdir -p "$results"
results="$(cd "$results" && pwd)"
native() { if command -v cygpath >/dev/null; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

# The three heaviest fixtures, which run Program.Main, get a process of their own; the TableQuench fixtures run
# beside the smaller assemblies; everything else is the complement of both, so a new fixture cannot be left out.
heavy='FullyQualifiedName~.DeployPathTableFeatureTests|FullyQualifiedName~.SchemaTemplateHappyPathTests|FullyQualifiedName~.TemplateTargetsHappyPathTests'
not_heavy='FullyQualifiedName!~.DeployPathTableFeatureTests&FullyQualifiedName!~.SchemaTemplateHappyPathTests&FullyQualifiedName!~.TemplateTargetsHappyPathTests'
table_quench='FullyQualifiedName~.TableQuench_'

quench=SchemaQuench/SchemaQuench.IntegrationTests
copy_build() {
  local dest="$results/.bin/$1"
  rm -rf "$dest"; mkdir -p "$dest"
  cp -r "$quench/bin/Debug/net10.0/." "$dest/"
  printf '%s' "$dest/SchemaQuench.IntegrationTests.dll"
}

status_dir="$results/.status"; mkdir -p "$status_dir"

packages="$(dotnet nuget locals global-packages --list | sed 's/^global-packages: //' | tr -d '')"
coverlet_version="$(grep -o 'coverlet.collector" Version="[^"]*"' Directory.Packages.props | grep -o '[0-9][0-9.]*')"
collector_dir="${packages%/}/coverlet.collector/$coverlet_version/build/netstandard2.0"
command -v cygpath >/dev/null && collector_dir="$(cygpath -u "$collector_dir")"
if [ ! -d "$collector_dir" ]; then
  echo "WARNING: coverage collector not found at $collector_dir -- the copied-binary processes will report no coverage"
  collector_dir=""
fi
run() {
  local name=$1 target=$2 filter=$3 tmp="$results/.tmp/$1"
  mkdir -p "$tmp"
  local start=$SECONDS
  local build=()
  [[ "$target" == *.csproj ]] && build=(--no-build -c Debug)
  # A copied .dll is not a project, so dotnet test cannot find the coverage collector, which lives in the NuGet cache,
  # not the build output: without this the collector is "not found" and that process reports no coverage at all.
  [[ "$target" == *.dll && -n "$collector_dir" ]] && build=(--test-adapter-path "$(native "$collector_dir")")
  TMP="$(native "$tmp")" TEMP="$(native "$tmp")" TMPDIR="$tmp" \
    dotnet test "$target" "${build[@]}" --filter "Category=SqlServer&$filter" --results-directory "$results" \
      --logger "trx;LogFileName=$name.trx" "${extra[@]}" \
      -- NUnit.TestOutputXml="nunit/$name" NUnit.OutputXmlFolderMode=RelativeToResultDirectory \
      > "$results/$name.log" 2>&1
  local rc=$?
  echo "$rc" > "$status_dir/$name"
  echo "$name: exit $rc, $((SECONDS - start))s -- $(grep -E '^(Passed|Failed)!' "$results/$name.log" | sed 's/ - .*//')"
  grep -E '^\s+Failed [A-Za-z]' "$results/$name.log" | sed "s/^/  $name:/"
}

q1="$(copy_build shard1)"; q2="$(copy_build shard2)"; q3="$(copy_build shard3)"
started=$SECONDS
run quench-heavy "$q1" "($heavy)" &
# Balanced by measured time (local, 2026-10-10): heavy ~590s; rest + SchemaTongs + DataTongs ~650s; TableQuench + Schema
# ~635s. The slowest process sets the wall time, so rebalance here when a suite grows.
(
  run quench-rest "$q2" "$not_heavy&FullyQualifiedName!~.TableQuench_"
  run schematongs SchemaTongs/SchemaTongs.IntegrationTests/SchemaTongs.IntegrationTests.csproj "FullyQualifiedName!~.__none__"
  run datatongs DataTongs/DataTongs.IntegrationTests/DataTongs.IntegrationTests.csproj "FullyQualifiedName!~.__none__"
) &
(
  run quench-tablequench "$q3" "$table_quench"
  run schema Schema/Schema.IntegrationTests/Schema.IntegrationTests.csproj "FullyQualifiedName!~.__none__"
) &
wait
echo "SQL Server shards: $((SECONDS - started))s wall; results in $results"

failed=0
for f in "$status_dir"/*; do [ "$(cat "$f")" = 0 ] || failed=1; done
rm -rf "$results/.bin" "$results/.tmp" "$status_dir"
exit $failed

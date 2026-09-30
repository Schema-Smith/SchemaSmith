#!/usr/bin/env bash
# Paths below are repo-relative; this anchors the script so it runs from anywhere.
cd "$(dirname "$0")/.." || exit 1
# Modern-band SQL Server sweep. Points the real integration suite at each modern SQL Server band in
# turn, using containers -- the bands where a Linux image exists.
#
# The rationale INVERTED in v2.7.0 and this header used to still claim the old one: CI then ran a
# single SQL Server leg, so 2017/2022/2025 were covered nowhere. CI now pins all four (see :42 below
# and continuous-integration.yml), so this sweep is a local pre-PR check against what CI will run,
# not the only place those bands are tested at all.
#
# DELIBERATELY SEPARATE FROM run-genuine-sweep.sh, and not folded into it:
#   run-genuine-sweep.sh is the PRE-PR gate. It takes minutes and must keep taking minutes, because the
#   failure mode that item exists to prevent is somebody SKIPPING it -- two 2017-vs-2016 defects reached
#   the v2.5.0 cut exactly that way. Bolting three full integration runs onto it would make it an
#   hours-long gate, and an hours-long gate before every PR gets skipped, which trades a known gap for
#   a worse one.
#   This script is the PRE-RELEASE breadth pass instead: run once per release, where hours are affordable.
#
# The bands are provisioned the way CI provisions its SQL Server: stock images ship NO full-text
# component, and every SQL Server fixture needs one. Without it each band failed 464/464 SchemaQuench
# tests in OneTimeSetUp with error 7609 -- a setup failure that reads exactly like a product failure.
# So each band installs mssql-server-fts, restarts, and provisions the semantic language statistics
# database before a single test runs. Any of those failing records the band as NOT RUN; none of them
# is allowed to degrade into a quiet pass.
#
# 2017 is deliberately still here. It was added because it caught a specific issue, and it can still
# be provisioned: 2017-latest is built on Ubuntu 18.04 (not 16.04), packages.microsoft.com still
# serves the 18.04/mssql-server-2017 list, and mssql-server-fts 14.0.3540.1 installs cleanly on it.
#
# Each band gets its own container and port so a leftover container from another band cannot silently
# answer for it -- the same failure shape as an all-skip reading as a pass.
set -u

RECORD="docs/development/genuine-sweep-results.md"
SWEEP_SHA="$(git rev-parse --short HEAD)"
SWEEP_STARTED="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
SWEEP_ROWS=""
PASSWORD='SchemaSmith!Band2026'

# port:image -- covers every leg continuous-integration.yml runs for SQL Server, and is free to cover
# MORE. Local sweeps are a SUPERSET of CI, never a subset: CI's matrix is what we can afford on every
# push, this runs once per release. Same rule scripts/run-floor-sweep.sh follows for the other three
# engines -- keep both in sync when CI's legs move, and never by REMOVING a band here.
#
# It did not always. 2019 used to be excluded here because it was the ONLY SQL Server leg CI ran, so
# this sweep existed to cover what CI did not. v2.7.0 gave SQL Server a full matrix (2017, 2019, 2022
# and 2025, every one of them pinned) and that inverted the rationale without anyone updating this list, which
# left 2019 and latest as CI legs NO local sweep certified -- exactly the hole the floor sweep exists
# to close for MySQL and PostgreSQL. Release prep should fail here rather than on merge.
#
# NO FLOATING BAND. `latest` and `2025-latest` resolve to the same digest, so running both is one
# ~1.5 GB boot and one full suite spent twice for a single result. Whether the ceiling has MOVED is a
# comparison, not a test run, and continuous-integration.yml's ceiling ratchet already makes it --
# including that the declared ceiling has a pinned leg. When it fires, add the band here.
BANDS="14340:2017-latest 14341:2019-latest 14342:2022-latest 14345:2025-latest"

cleanup() {
  for band in $BANDS; do
    docker rm -f "ss-band-${band%%:*}" >/dev/null 2>&1
  done
}
trap cleanup EXIT

for band in $BANDS; do
  port="${band%%:*}"
  image="mcr.microsoft.com/mssql/server:${band##*:}"
  name="ss-band-$port"
  echo "===== band $image on $port ====="

  docker rm -f "$name" >/dev/null 2>&1
  if ! docker run -d --name "$name" -p "$port:1433" \
       -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$PASSWORD" "$image" >/dev/null; then
    echo "  could not start $image -- recording as NOT RUN rather than skipping silently"
    SWEEP_ROWS="${SWEEP_ROWS}| $image @ $port | CONTAINER FAILED TO START | full SqlServer category |
"
    continue
  fi

  # Full-Text Search is NOT in the stock image, and the SQL Server fixtures require it -- without it
  # every SchemaQuench test dies in OneTimeSetUp with 7609 and the whole band reads as a product
  # failure. CI installs it explicitly; so must this. The product line is derived from the image's
  # Ubuntu release, exactly as demoserver.Dockerfile does it, with 18.04 -> 2017 added: 2017-latest is
  # built on 18.04 (not 16.04), and packages.microsoft.com still serves that list.
  echo "  installing full-text search"
  if ! docker exec -u 0 "$name" sh -c '
      set -e
      . /etc/os-release
      case "$VERSION_ID" in
        18.04) PROD=2017 ;;
        20.04) PROD=2019 ;;
        22.04) PROD=2022 ;;
        24.04) PROD=2025 ;;
        *) echo "unsupported Ubuntu base $VERSION_ID for mssql-server-fts" >&2; exit 1 ;;
      esac
      command -v curl >/dev/null || { apt-get update -qq; apt-get install -y -qq curl; }
      curl -fsSL "https://packages.microsoft.com/config/ubuntu/${VERSION_ID}/mssql-server-${PROD}.list" \
        -o /etc/apt/sources.list.d/mssql-server.list
      apt-get update -qq
      DEBIAN_FRONTEND=noninteractive apt-get install -y -qq mssql-server-fts
  ' >/dev/null 2>&1; then
    echo "  full-text install FAILED -- recording as NOT RUN rather than certifying without it"
    SWEEP_ROWS="${SWEEP_ROWS}| $image @ $port | FULL-TEXT INSTALL FAILED | full SqlServer category |
"
    docker rm -f "$name" >/dev/null 2>&1
    continue
  fi
  docker restart "$name" >/dev/null


  # sqlcmd moved between images: 2017 ships /opt/mssql-tools (no -C flag -- it predates the
  # mandatory-TLS client), while 2022+ ship /opt/mssql-tools18, which REQUIRES -C to trust the
  # self-signed cert. Hardcoding either one makes the other band report NEVER READY forever, a false
  # negative indistinguishable from a broken build. Detect rather than assume.
  # `docker exec "$name" test -x /opt/...` does NOT work from Git Bash, which is where this actually
  # gets run: MSYS rewrites a bare /opt/... argv entry into a Windows path before docker sees it, so the
  # test fails on 2022 and the script silently picks the 2017 tooling. Wrapping it in sh -c keeps the
  # path inside a string argument, which MSYS leaves alone. Verified both ways on both image generations.
  #
  # THE SAME REWRITE APPLIES TO EVERY container-side path below, not just this probe. It was
  # originally fixed here only, so $SQLCMD was still passed as a bare argv entry to the readiness
  # and version queries -- docker received "C:/Program Files/Git/opt/mssql-tools18/bin/sqlcmd",
  # every band reported NEVER READY, and the sweep exited 0 having run no tests at all. Keep every
  # container-side path inside an sh -c string.
  if docker exec "$name" sh -c 'test -x /opt/mssql-tools18/bin/sqlcmd' 2>/dev/null; then
    SQLCMD="/opt/mssql-tools18/bin/sqlcmd"; TLS="-C"
  else
    SQLCMD="/opt/mssql-tools/bin/sqlcmd"; TLS=""
  fi

  # Wait for the instance rather than sleeping a fixed amount: 2022 has markedly the slowest first boot
  # of the three (Demos/README.md), so any single sleep is either too short for it or wasted on the others.
  ready=0
  for _ in $(seq 1 60); do
    if docker exec "$name" sh -c \
         "$SQLCMD -S localhost -U sa -P '$PASSWORD' $TLS -Q 'SELECT 1'" >/dev/null 2>&1; then
      ready=1; break
    fi
    sleep 5
  done
  if [ "$ready" -ne 1 ]; then
    echo "  never became ready"
    SWEEP_ROWS="${SWEEP_ROWS}| $image @ $port | NEVER READY | full SqlServer category |
"
    docker rm -f "$name" >/dev/null 2>&1
    continue
  fi

  # STATISTICAL_SEMANTICS full-text columns cannot be created without this, so the tests covering them
  # would SKIP rather than fail -- and a test that only ever skips proves nothing, which is the exact
  # failure mode this whole sweep exists to catch. Same script CI uses.
  # Best-effort, deliberately NOT fatal. On the 2017 image this cannot succeed: mssql-server-fts ships
  # a semanticsdb.bak stamped 14.00.0700 while the engine is 14.00.3540, so the restore is refused with
  # Msg 3169 -- the backup shipped with the server cannot be restored into that server. Failing the band
  # for that would make 2017 permanently uncertifiable over a packaging artifact that is not ours, and
  # dropping 2017 is not an option: it is in the bands because it caught a specific issue.
  #
  # The trade is that a band can now pass having skipped a feature, which is the exact shape this sweep
  # exists to catch -- so the row is NOT allowed to read as a bare "Passed!". It carries the gap, and
  # the reason is captured rather than discarded (the first failure arrived with no explanation because
  # this went to /dev/null).
  SEMANTIC_NOTE=""
  if ! bash scripts/provision-semantic-db.sh "$name" sa "$PASSWORD" \
       > "/tmp/ss-band-$port-semantic.log" 2>&1; then
    SEMANTIC_NOTE=" + SEMANTIC COVERAGE SKIPPED (no semanticsdb)"
    echo "  semantic DB unavailable -- STATISTICAL_SEMANTICS will self-skip. Reason:"
    grep -E "^Msg|error|Error" "/tmp/ss-band-$port-semantic.log" | head -3 | sed "s/^/    /"
  fi


  version="$(docker exec "$name" sh -c \
      "$SQLCMD -S localhost -U sa -P '$PASSWORD' $TLS -h -1 -W -Q \"SET NOCOUNT ON; SELECT CONVERT(varchar(20), SERVERPROPERTY('ProductVersion'))\"" \
      2>/dev/null | tr -d '\r' | head -1)"
  echo "  ready: $version"

  SmithySettings_SqlServer__Server=127.0.0.1 SmithySettings_SqlServer__Port=$port \
  SmithySettings_SqlServer__User=sa SmithySettings_SqlServer__Password="$PASSWORD" \
  dotnet test SchemaSmith.sln --filter "Category=SqlServer" 2>&1 \
    | tee "/tmp/ss-band-$port.log" | grep -E "^(Passed!|Failed!|No test)|^  Failed "

  # Every assembly, not just the first. `head -1` recorded whichever finished first, so a passing
  # Schema.IntegrationTests masked 464 failed SchemaQuench tests in the very same band -- and the row
  # then read "Passed!", which also starved the Failed! exit guard of anything to catch.
  # awk rather than bc: bc is not present in Git Bash, where this runs.
  band_totals="$(grep -E "^(Passed!|Failed!)" "/tmp/ss-band-$port.log" | awk '
    { for (i = 1; i <= NF; i++) {
        if ($i == "Failed:") f += $(i + 1);
        if ($i == "Passed:") p += $(i + 1);
      }
      n++ }
    END { printf "%d %d %d", f + 0, p + 0, n + 0 }')"
  band_failed="${band_totals%% *}"
  band_assemblies="${band_totals##* }"
  band_passed="$(printf "%s" "$band_totals" | cut -d" " -f2)"

  if [ -z "$band_totals" ] || [ "$band_assemblies" -eq 0 ]; then
    summary="NO RESULT"
  elif [ "$band_failed" -gt 0 ]; then
    summary="Failed! - $band_failed failed, $band_passed passed across $band_assemblies assemblies"
  else
    summary="Passed! - 0 failed, $band_passed passed across $band_assemblies assemblies"
  fi
  SWEEP_ROWS="${SWEEP_ROWS}| $image ($version) @ $port | ${summary:-NO RESULT}$SEMANTIC_NOTE | full SqlServer category, 0 failed |
"
  docker rm -f "$name" >/dev/null 2>&1
done

# Appended to the same record the pre-PR sweep writes, so one file answers "what was this commit
# actually exercised against" across both passes.
mkdir -p "$(dirname "$RECORD")"
{
  echo ""
  echo "## $SWEEP_STARTED - modern bands - commit $SWEEP_SHA"
  echo ""
  echo "| Target | Result | Expected |"
  echo "|---|---|---|"
  printf "%s" "$SWEEP_ROWS"
} >> "$RECORD"
echo
echo "Recorded in $RECORD for commit $SWEEP_SHA -- commit it alongside the release it certifies."

# A band that never became ready, failed to start, or produced no result has NOT been swept, and the
# sweep must not report success for it. Exiting 0 on an all-NEVER-READY run is how a broken probe
# looked exactly like a clean pass -- the record said so plainly while the exit code said otherwise,
# and the exit code is what a caller (or a hook) actually reads.
if printf "%s" "$SWEEP_ROWS" | grep -qE "NEVER READY|CONTAINER FAILED TO START|FULL-TEXT INSTALL FAILED|NO RESULT|Failed!"; then
  echo "FAIL: at least one band did not run. The rows above are not a certification."
  exit 1
fi
if [ -z "$SWEEP_ROWS" ]; then
  echo "FAIL: no bands were attempted at all."
  exit 1
fi
echo "All bands ran."

#!/usr/bin/env python3
# Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
"""Where integration-suite time goes: inside tests, or in fixture setup and teardown.

Reads the NUnit result XML that `dotnet test -- NUnit.TestOutputXml=<dir>` writes (one file per assembly). A
fixture's duration includes its OneTimeSetUp and OneTimeTearDown, and a namespace suite's includes its SetUpFixture,
so each level's duration minus its children's is what it spent outside them. TRX cannot show this: it records only
test cases.

With parallel fixtures a fixture's first test can wait for a worker, and that wait reads as setup. For clean
numbers run one engine with one worker: `dotnet test ... --filter Category=<engine> -- NUnit.NumberOfTestWorkers=1`.

Usage: suite-overhead.py <xml file or directory>... [--top N] [--markdown]
"""
import argparse
import collections
import datetime
import glob
import os
import sys
import xml.etree.ElementTree as ET

ENGINES = ("SqlServer", "PostgreSQL", "MySQL", "MariaDb")


def engine_of(fullname):
    parts = fullname.split(".")
    return next((e for e in ENGINES if e in parts), "Other")


def duration(node):
    return float(node.get("duration") or 0)


def stamp(node, attr):
    value = node.get(attr)
    if not value:
        return None
    value = value.rstrip("Z")
    if "." in value:
        head, frac = value.split(".", 1)
        value = f"{head}.{frac[:6]}"
    return datetime.datetime.fromisoformat(value)


def walk(node, assembly, fixtures, setups):
    """Collect each TestFixture's time and each namespace/SetUpFixture level's time outside its children."""
    children = [c for c in node if c.tag in ("test-suite", "test-case")]
    kind = node.get("type")
    if kind in ("TestFixture", "ParameterizedFixture", "GenericFixture"):
        cases = list(node.iter("test-case"))
        ran = [c for c in cases if c.get("result") != "Skipped"]
        in_tests = sum(duration(c) for c in cases)
        # Setup is the fixture's start to its first test's start, teardown its last test's end to its own end. The
        # rest of its span is time between tests -- with parallel fixtures, mostly waiting for a worker or a lock,
        # which is not setup and must not be reported as it.
        starts = [s for s in (stamp(c, "start-time") for c in cases) if s]
        ends = [e for e in (stamp(c, "end-time") for c in cases) if e]
        f_start, f_end = stamp(node, "start-time"), stamp(node, "end-time")
        setup = (min(starts) - f_start).total_seconds() if starts and f_start else 0.0
        teardown = (f_end - max(ends)).total_seconds() if ends and f_end else 0.0
        fixtures.append({
            "assembly": assembly, "name": node.get("fullname"), "engine": engine_of(node.get("fullname") or ""),
            "wall": duration(node), "tests": in_tests, "count": len(ran),
            "setup": max(setup, 0.0), "teardown": max(teardown, 0.0),
        })
        return
    if kind in ("SetUpFixture", "TestSuite") and node.get("fullname"):
        outside = duration(node) - sum(duration(c) for c in children)
        if outside > 0.05:
            setups.append({"assembly": assembly, "name": node.get("fullname"),
                           "engine": engine_of(node.get("fullname")), "outside": outside})
    for c in children:
        if c.tag == "test-suite":
            walk(c, assembly, fixtures, setups)


def load(paths):
    files = []
    for p in paths:
        files += sorted(glob.glob(os.path.join(p, "**", "*.xml"), recursive=True)) if os.path.isdir(p) else [p]
    fixtures, setups, assemblies = [], [], []
    for f in files:
        root = ET.parse(f).getroot()
        for asm in root.iter("test-suite"):
            if asm.get("type") == "Assembly":
                name = os.path.basename(asm.get("name") or f)
                assemblies.append((name, duration(asm)))
                for c in asm:
                    if c.tag == "test-suite":
                        walk(c, name, fixtures, setups)
    return fixtures, setups, assemblies


def report(fixtures, setups, assemblies, top, md):
    """Only what the XML measures reliably: each test's duration and each fixture's END. A fixture's start is stamped
    when it is dispatched, not when its setup begins, so per-fixture setup is not reported; setup appears only as the
    remainder of an assembly's wall time."""
    out = []
    say = out.append
    say("## Suite time" if md else "Suite time")
    say("")
    per_asm = collections.defaultdict(lambda: {"tests": 0.0, "teardown": 0.0, "count": 0})
    for f in fixtures:
        a = per_asm[f["assembly"]]
        a["tests"] += f["tests"]; a["teardown"] += f["teardown"]; a["count"] += f["count"]
    if md:
        say("| Assembly | Wall | In tests | Fixture teardown | Setup and other | Tests |")
        say("|---|---:|---:|---:|---:|---:|")
    else:
        say(f"  {'assembly':38s} {'wall':>7s} {'in tests':>9s} {'teardown':>9s} {'setup+other':>12s} {'tests':>6s}")
    parallel = False
    for name, wall in assemblies:
        a = per_asm.get(name, {"tests": 0.0, "teardown": 0.0, "count": 0})
        rest = wall - a["tests"] - a["teardown"]
        if rest < 0:
            parallel = True
        rest_text = f"{rest:.0f}s" if rest >= 0 else "overlap"
        row = (name, wall, a["tests"], a["teardown"], rest_text, a["count"])
        say("| %s | %.0fs | %.0fs | %.0fs | %s | %d |" % row if md else "  %-38s %6.0fs %8.0fs %8.0fs %12s %6d" % row)
    say("")
    say("Setup and other = wall - in tests - teardown: fixture setup, engine-level setup and the gaps between tests."
        + (" \"overlap\": tests ran in parallel, so their durations add up to more than the wall time." if parallel else ""))
    say("")
    by = collections.defaultdict(lambda: {"tests": 0.0, "count": 0, "fixtures": 0, "teardown": 0.0})
    for f in fixtures:
        b = by[f["engine"]]
        b["tests"] += f["tests"]; b["count"] += f["count"]; b["fixtures"] += 1; b["teardown"] += f["teardown"]
    if md:
        say("| Engine | Fixtures | Tests | In tests | Fixture teardown | Per test |")
        say("|---|---:|---:|---:|---:|---:|")
    else:
        say(f"  {'engine':10s} {'fixtures':>8s} {'tests':>6s} {'in tests':>9s} {'teardown':>9s} {'per test':>9s}")
    for e in sorted(by, key=lambda k: -by[k]["tests"]):
        b = by[e]
        per = b["tests"] / b["count"] if b["count"] else 0
        row = (e, b["fixtures"], b["count"], b["tests"], b["teardown"], per)
        say("| %s | %d | %d | %.0fs | %.0fs | %.2fs |" % row if md else "  %-10s %8d %6d %8.0fs %8.0fs %8.2fs" % row)
    say("")
    worst = sorted(fixtures, key=lambda f: -(f["tests"] + f["teardown"]))[:top]
    say(f"### Top {top} fixtures by time in tests + teardown" if md else f"Top {top} fixtures by time in tests + teardown:")
    if md:
        say("| Fixture | Engine | Total | In tests | Teardown | Tests |")
        say("|---|---|---:|---:|---:|---:|")
    for f in worst:
        short = f["name"].split(".")[-1]
        row = (short, f["engine"], f["tests"] + f["teardown"], f["tests"], f["teardown"], f["count"])
        say("| %s | %s | %.1fs | %.1fs | %.1fs | %d |" % row if md
            else "  %-46s %-10s %7.1fs  (in tests %6.1fs, teardown %5.1fs, %3d tests)" % row)
    return "\n".join(out)


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("paths", nargs="+")
    ap.add_argument("--top", type=int, default=15)
    ap.add_argument("--markdown", action="store_true")
    a = ap.parse_args()
    fixtures, setups, assemblies = load(a.paths)
    if not fixtures:
        print("no NUnit result XML with fixtures found", file=sys.stderr)
        return 1
    print(report(fixtures, setups, assemblies, a.top, a.markdown))
    return 0


if __name__ == "__main__":
    sys.exit(main())

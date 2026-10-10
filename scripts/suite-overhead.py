#!/usr/bin/env python3
# Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
"""Where integration-suite time goes: inside tests, or in fixture setup and teardown.

Reads the NUnit result XML that `dotnet test -- NUnit.TestOutputXml=<dir>` writes (one file per assembly). A
fixture's duration includes its OneTimeSetUp and OneTimeTearDown, and a namespace suite's includes its SetUpFixture,
so each level's duration minus its children's is what it spent outside them. TRX cannot show this: it records only
test cases.

Usage: suite-overhead.py <xml file or directory>... [--top N] [--markdown]
"""
import argparse
import collections
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


def walk(node, assembly, fixtures, setups):
    """Collect each TestFixture's time and each namespace/SetUpFixture level's time outside its children."""
    children = [c for c in node if c.tag in ("test-suite", "test-case")]
    kind = node.get("type")
    if kind in ("TestFixture", "ParameterizedFixture", "GenericFixture"):
        cases = list(node.iter("test-case"))
        ran = [c for c in cases if c.get("result") != "Skipped"]
        in_tests = sum(duration(c) for c in cases)
        fixtures.append({
            "assembly": assembly, "name": node.get("fullname"), "engine": engine_of(node.get("fullname") or ""),
            "wall": duration(node), "tests": in_tests, "count": len(ran),
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
        files += sorted(glob.glob(os.path.join(p, "*.xml"))) if os.path.isdir(p) else [p]
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
    out = []
    say = out.append
    say("## Suite time: inside tests vs. fixture setup and teardown" if md else "Suite time: inside tests vs. fixture setup and teardown")
    say("")
    if md:
        say("| Assembly | Wall |")
        say("|---|---:|")
    for name, wall in assemblies:
        say(f"| {name} | {wall:.0f}s |" if md else f"  {name:40s} {wall:8.0f}s")
    say("")
    by = collections.defaultdict(lambda: {"wall": 0.0, "tests": 0.0, "count": 0, "fixtures": 0})
    for f in fixtures:
        b = by[f["engine"]]
        b["wall"] += f["wall"]; b["tests"] += f["tests"]; b["count"] += f["count"]; b["fixtures"] += 1
    level = collections.defaultdict(float)
    for s in setups:
        level[s["engine"]] += s["outside"]
    if md:
        say("| Engine | Fixtures | Tests | In tests | Fixture setup/teardown | Engine-level setup | Per test |")
        say("|---|---:|---:|---:|---:|---:|---:|")
    else:
        say(f"  {'engine':10s} {'fixtures':>8s} {'tests':>6s} {'in tests':>10s} {'fixture s/t':>12s} {'engine setup':>13s} {'per test':>9s}")
    for e in sorted(by, key=lambda k: -by[k]["wall"]):
        b = by[e]
        overhead = b["wall"] - b["tests"]
        per = b["tests"] / b["count"] if b["count"] else 0
        row = (e, b["fixtures"], b["count"], b["tests"], overhead, level.get(e, 0.0), per)
        say("| %s | %d | %d | %.0fs | %.0fs | %.0fs | %.2fs |" % row if md
            else "  %-10s %8d %6d %9.0fs %11.0fs %12.0fs %8.2fs" % row)
    say("")
    worst = sorted(fixtures, key=lambda f: -(f["wall"] - f["tests"]))[:top]
    say(f"### Top {top} fixtures by setup/teardown" if md else f"Top {top} fixtures by setup/teardown:")
    if md:
        say("| Fixture | Engine | Setup/teardown | In tests | Tests |")
        say("|---|---|---:|---:|---:|")
    for f in worst:
        short = f["name"].split(".")[-1]
        row = (short, f["engine"], f["wall"] - f["tests"], f["tests"], f["count"])
        say("| %s | %s | %.1fs | %.1fs | %d |" % row if md else "  %-50s %-10s %7.1fs setup/teardown %7.1fs in %3d tests" % row)
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

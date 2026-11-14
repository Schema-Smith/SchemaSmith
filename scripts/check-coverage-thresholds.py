# Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
"""Fail the build if any product assembly or the solution total is below its line-coverage threshold.

Reads a merged Cobertura report (ReportGenerator output) and checks per-package line coverage.
Thresholds are a ratchet: raised over time, never lowered to make a red gate green
(see the CONTRIBUTING.md Code Coverage section).

Usage:
  check-coverage-thresholds.py <merged-cobertura.xml>                # enforce (exit 1 on breach)
  check-coverage-thresholds.py <merged-cobertura.xml> --report-only  # print only, always exit 0
"""
import sys
import xml.etree.ElementTree as ET

# Per-project line-coverage thresholds (percent). Solution is computed from these packages only.
# Calibrated to ~1-2pt under the observed CI baseline.
THRESHOLDS = {
    "DataTongs": 92.0,
    "Schema": 93.0,
    "SchemaQuench": 91.0,
    "SchemaShears": 98.0,
    "SchemaTongs": 90.0,
}
SOLUTION_THRESHOLD = 91.0


def main(path, report_only):
    root = ET.parse(path).getroot()
    results = {}
    sol_cov = sol_tot = 0
    seen = set()
    unknown = set()
    for pkg in root.findall(".//package"):
        name = pkg.get("name")
        lines = pkg.findall(".//line")
        tot = len(lines)
        cov = sum(1 for ln in lines if int(ln.get("hits")) > 0)
        if name in THRESHOLDS:
            seen.add(name)
            results[name] = (cov, tot)
            sol_cov += cov
            sol_tot += tot
        else:
            unknown.add(name)

    # Every product assembly has a threshold, so a package with none is a new assembly that would otherwise drop out of
    # the solution total unnoticed (or one coverage.runsettings should exclude).
    failures = [f"{name}: no threshold -- add one here (and in CONTRIBUTING.md), or exclude it in coverage.runsettings"
                for name in sorted(unknown)]
    for name in sorted(unknown):
        print(f"{name}: in the coverage report with no threshold")
    print("\nProject               line%    covered/total   threshold")
    print("-" * 60)
    for name, thr in THRESHOLDS.items():
        if name not in seen:
            failures.append(f"{name}: MISSING from coverage report")
            print(f"{name:20} MISSING")
            continue
        cov, tot = results[name]
        pct = (cov / tot * 100) if tot else 0.0
        status = "OK" if pct >= thr else "FAIL"
        print(f"{name:20}{pct:6.1f}%  {cov:>7}/{tot:<7}   >= {thr:.0f}%  {status}")
        if pct < thr:
            failures.append(f"{name}: {pct:.1f}% < {thr:.0f}%")

    sol_pct = (sol_cov / sol_tot * 100) if sol_tot else 0.0
    sol_status = "OK" if sol_pct >= SOLUTION_THRESHOLD else "FAIL"
    print("-" * 60)
    print(f"{'SOLUTION':20}{sol_pct:6.1f}%  {sol_cov:>7}/{sol_tot:<7}   >= {SOLUTION_THRESHOLD:.0f}%  {sol_status}")
    if sol_pct < SOLUTION_THRESHOLD:
        failures.append(f"SOLUTION: {sol_pct:.1f}% < {SOLUTION_THRESHOLD:.0f}%")

    if failures:
        print("\nCOVERAGE GATE: would FAIL on:")
        for f in failures:
            print(f"  - {f}")
        if report_only:
            print("\n(report-only mode — not failing the build)")
            return
        sys.exit(1)
    print("\nCoverage gate passed.")


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if a != "--report-only"]
    report_only = "--report-only" in sys.argv[1:]
    if len(args) != 1:
        print("Usage: check-coverage-thresholds.py <merged-cobertura.xml> [--report-only]", file=sys.stderr)
        sys.exit(2)
    main(args[0], report_only)

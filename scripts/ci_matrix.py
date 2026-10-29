#!/usr/bin/env python3
# Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
"""The integration legs continuous-integration.yml runs on a merge to main, read from the workflow itself.

    python3 scripts/ci_matrix.py <engine>        engine: sqlserver | postgres | mysql | mariadb | lctn1

Prints one leg per line, tab-separated: image, tag, and a third field where the leg has one (the SQL Server server
collation; the lctn1 test category). The local sweeps take their legs from here rather than from a copy, so a leg CI
adds is a leg they run or refuse to certify without, and an image CI pins is the image they test.
"""
import json
import os
import re
import sys

WORKFLOW = '.github/workflows/continuous-integration.yml'
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def main_list(text, key, start=0):
    """The merge-to-main side of `key: ${{ github.event_name == 'pull_request' && fromJSON('<pr>') || fromJSON('<main>') }}`."""
    m = re.compile(r"^\s+" + key + r": \$\{\{ github\.event_name == 'pull_request' && fromJSON\('(.*?)'\) "
                   r"\|\| fromJSON\('(.*?)'\) \}\}", re.M).search(text, start)
    if not m:
        raise SystemExit(f"ci_matrix: no '{key}' matrix found in {WORKFLOW}; the workflow's shape changed")
    return json.loads(m.group(2))


def legs(text, engine):
    if engine == 'sqlserver':
        job = text.index('integration-tests-sqlserver')
        for leg in main_list(text, 'include', job):
            image, tag = leg['image'].rsplit(':', 1)
            yield image, tag, leg.get('collation', 'SQL_Latin1_General_CP1_CI_AS')
    elif engine == 'postgres':
        for v in main_list(text, 'pg'):
            yield 'postgres', v, ''
    elif engine in ('mysql', 'mariadb'):
        for v in main_list(text, engine):
            yield engine, v, ''
    elif engine == 'lctn1':
        job = text.index('integration-tests-lctn1')
        m = re.compile(r"\|\| '(\[.*?\])'\) \}\}", re.S).search(text, job)
        if not m:
            raise SystemExit(f"ci_matrix: no lctn1 matrix found in {WORKFLOW}; the workflow's shape changed")
        for leg in json.loads(m.group(1)):
            yield leg['image'], leg['version'], leg['category']
    else:
        raise SystemExit(f"ci_matrix: unknown engine '{engine}' (sqlserver, postgres, mysql, mariadb, lctn1)")


if __name__ == '__main__':
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    with open(os.path.join(ROOT, WORKFLOW), encoding='utf-8') as f:
        workflow = f.read()
    found = list(legs(workflow, sys.argv[1]))
    if not found:
        raise SystemExit(f"ci_matrix: the {sys.argv[1]} matrix is empty")
    # The sweeps read the last field up to the newline, so Windows' \r\n would leave a \r in it.
    sys.stdout.reconfigure(newline='\n')
    for row in found:
        print('\t'.join(row))

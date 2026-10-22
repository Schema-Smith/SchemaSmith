#!/usr/bin/env python3
# Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
"""Does a change touch a path CI tests?

    python3 scripts/ci_path_filter.py .github/ci-tested-paths.txt < changed-files

Reads one path per line on stdin and exits 0 when any of them matches the [tested] section of the file (CI must
run), 1 when none does (doc-only to CI). The patterns use GitHub's path-filter syntax, evaluated in order as GitHub
evaluates a workflow's `paths`: the last pattern that matches a path decides, `!` excludes, `*` stays within one
directory and `**` crosses them. `--selftest` checks the matcher against cases whose GitHub behaviour is known.
"""
import re
import sys


def load_patterns(path):
    patterns, section = [], None
    with open(path, encoding='utf-8') as f:
        for raw in f:
            line = raw.strip()
            if not line or line.startswith('#'):
                continue
            if line.startswith('[') and line.endswith(']'):
                section = line[1:-1]
                continue
            if section == 'tested':
                patterns.append(line)
    return patterns


def to_regex(glob):
    out, i = [], 0
    while i < len(glob):
        if glob.startswith('**/', i):
            out.append('(?:.*/)?')
            i += 3
        elif glob.startswith('**', i):
            out.append('.*')
            i += 2
        elif glob[i] == '*':
            out.append('[^/]*')
            i += 1
        elif glob[i] == '?':
            out.append('[^/]')
            i += 1
        else:
            out.append(re.escape(glob[i]))
            i += 1
    return re.compile('^' + ''.join(out) + '$')


def compile_patterns(patterns):
    return [(p.startswith('!'), to_regex(p[1:] if p.startswith('!') else p)) for p in patterns]


def is_tested(path, compiled):
    tested = False
    for negated, rx in compiled:
        if rx.match(path):
            tested = not negated
    return tested


def selftest():
    compiled = compile_patterns(['Schema/**', 'Demos/**', '!Demos/**/*.sql', 'Demos/**/Templates/**/*.sql',
                                 '!**/*.md', 'docs/end-user/reference/schemaquench.md', '*.sln'])
    cases = {
        'Schema/Scripts/SqlServer/x.sql': True,
        'Schema/README.md': False,
        'Demos/SqlServer/Pkg/Templates/Main/Procedures/p.sql': True,
        'Demos/Learn/course6-setup/seed/mysql/shop.sql': False,
        'Demos/SqlServer/Pkg/Product.json': True,
        'Demos/README.md': False,
        'docs/end-user/reference/schemaquench.md': True,
        'docs/end-user/guide/01-why-schemasmith.md': False,
        'SchemaSmith.sln': True,
        'nested/SchemaSmith.sln': False,
        'Schemas/x.cs': False,
    }
    failed = [p for p, want in cases.items() if is_tested(p, compiled) != want]
    for p in failed:
        print(f'selftest: {p} should be {"tested" if cases[p] else "untested"}', file=sys.stderr)
    return 1 if failed else 0


def main(argv):
    if '--selftest' in argv:
        return selftest()
    compiled = compile_patterns(load_patterns(argv[1]))
    for line in sys.stdin:
        path = line.strip()
        if path and is_tested(path, compiled):
            print(f'CI tests: {path}')
            return 0
    return 1


if __name__ == '__main__':
    sys.exit(main(sys.argv))

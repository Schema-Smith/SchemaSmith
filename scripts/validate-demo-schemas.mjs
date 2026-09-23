// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
//
// Validates every shipped package file against its committed editor schema, in ONE process.
//
// WHY THIS EXISTS AS A SCRIPT. This check used to be a 105-entry GitHub Actions matrix -- one job per
// schema/file pair. Each job averaged 9 seconds of work, but GitHub bills a job a full minute, so the run cost
// ~105 billed minutes to do ~16 minutes of work: more than the entire 10-leg integration matrix. The pairs were
// never the expensive part; the per-job overhead was. Same coverage, one job, no per-pair job names.
//
// The pair list lives in validate-demo-schemas.json next to this script, so adding a package is a data edit.

import { readFile, readdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import Ajv from 'ajv';

const repoRoot = path.resolve(import.meta.dirname, '..');
const pairsFile = path.join(import.meta.dirname, 'validate-demo-schemas.json');

/** Turns a glob (**, *) into a RegExp anchored at the repo root. Only the two forms the pair list uses. */
function globToRegExp(glob) {
  let rx = '';
  for (let i = 0; i < glob.length; i++) {
    const c = glob[i];
    if (c === '*' && glob[i + 1] === '*') {
      // `**/` matches any number of directories, including none
      if (glob[i + 2] === '/') { rx += '(?:[^/]+/)*'; i += 2; } else { rx += '.*'; i += 1; }
    } else if (c === '*') {
      rx += '[^/]*';
    } else if ('.+?^${}()|[]\\'.includes(c)) {
      rx += '\\' + c;
    } else {
      rx += c;
    }
  }
  return new RegExp('^' + rx + '$');
}

async function walk(dir, out = []) {
  let entries;
  try {
    entries = await readdir(dir, { withFileTypes: true });
  } catch {
    return out;                     // a directory the pattern names but the repo does not have
  }
  for (const e of entries) {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) await walk(full, out);
    else out.push(full);
  }
  return out;
}

/** The fixed directory prefix of a glob, so a pattern walks its own subtree rather than the whole repo. */
function globRoot(glob) {
  const parts = glob.split('/');
  const fixed = [];
  for (const p of parts) {
    if (p.includes('*')) break;
    fixed.push(p);
  }
  return fixed.join('/');
}

const pairs = JSON.parse(await readFile(pairsFile, 'utf8'));
const ajv = new Ajv({ strict: false, allErrors: true });

let checked = 0;
const failures = [];
const emptyPatterns = [];

for (const pair of pairs) {
  const schemaPath = path.join(repoRoot, pair.schema);
  if (!existsSync(schemaPath)) {
    failures.push(`${pair.name}: schema not found -- ${pair.schema}`);
    continue;
  }
  let validate;
  try {
    validate = ajv.compile(JSON.parse(await readFile(schemaPath, 'utf8')));
  } catch (err) {
    failures.push(`${pair.name}: schema will not compile -- ${pair.schema}: ${err.message}`);
    continue;
  }

  // Many pairs name a single file (Product.json) rather than a glob; walking a file path finds nothing, which
  // would check zero files and still report success -- the exact silent hole this script has to avoid.
  let candidates;
  if (!pair.files.includes('*')) {
    const literal = path.join(repoRoot, pair.files);
    candidates = existsSync(literal) ? [literal] : [];
  } else {
    const rx = globToRegExp(pair.files);
    const root = path.join(repoRoot, globRoot(pair.files));
    candidates = (await walk(root)).filter(f =>
      rx.test(path.relative(repoRoot, f).split(path.sep).join('/')));
  }

  // A pattern that matches nothing is a silent hole -- the old matrix would report a green job for it too, but
  // here it is at least named.
  if (candidates.length === 0) emptyPatterns.push(`${pair.name}: no files matched ${pair.files}`);

  for (const file of candidates) {
    checked++;
    let data;
    try {
      data = JSON.parse(await readFile(file, 'utf8'));
    } catch (err) {
      failures.push(`${path.relative(repoRoot, file)}: not valid JSON -- ${err.message}`);
      continue;
    }
    if (!validate(data)) {
      for (const e of validate.errors) {
        failures.push(`${path.relative(repoRoot, file)} [${pair.name}]: ${e.instancePath || '/'} ${e.message}`);
      }
    }
  }
}

console.log(`validate-demo-schemas: ${pairs.length} schema/pattern pairs, ${checked} files checked`);
for (const note of emptyPatterns) console.log(`  note: ${note}`);

if (failures.length > 0) {
  console.error(`\n${failures.length} validation failure(s):`);
  for (const f of failures) console.error(`  ${f}`);
  process.exit(1);
}
console.log('all shipped package files match their committed schemas');

#!/usr/bin/env node
/**
 * FHIRBridge lint ratchet (no-regression) for ESLint + stylelint.
 *
 * The codebase has pre-existing lint debt; blocking on all of it would halt every
 * PR. Instead this fails only when the error count RISES above the committed
 * baseline (scripts/lint-baseline.json). As debt is fixed, re-baseline with
 *   `node scripts/lint-ratchet.mjs --update-baseline`  and commit.
 *
 * Linters are invoked via their CLI .js entrypoints through `node` so this works
 * identically on CI (Linux) and local Git Bash (Windows) — no shell glob issues.
 */
import { spawnSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const portal = resolve(here, '..');
const BASELINE = join(here, 'lint-baseline.json');
const bin = (...p) => join(portal, 'node_modules', ...p);

function jsonFrom(text, label) {
  const i = text.indexOf('[');
  const j = text.lastIndexOf(']');
  if (i < 0 || j < i) throw new Error(`${label}: no JSON in output`);
  return JSON.parse(text.slice(i, j + 1));
}
function nodeRun(args) {
  const r = spawnSync(process.execPath, args, {
    cwd: portal, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024,
  });
  // stylelint emits its JSON on stderr when it exits non-zero; eslint uses stdout.
  return (r.stdout || '') + '\n' + (r.stderr || '');
}

function eslintErrors() {
  const out = nodeRun([bin('@angular', 'cli', 'bin', 'ng.js'), 'lint', '--format', 'json']);
  return jsonFrom(out, 'eslint').reduce((a, f) => a + f.errorCount, 0);
}
function stylelintErrors() {
  const out = nodeRun([bin('stylelint', 'bin', 'stylelint.mjs'), 'src/**/*.scss', '--formatter', 'json']);
  let e = 0;
  for (const f of jsonFrom(out, 'stylelint')) for (const w of f.warnings) if (w.severity === 'error') e++;
  return e;
}

const eslint = eslintErrors();
const stylelint = stylelintErrors();

if (process.argv.includes('--update-baseline')) {
  writeFileSync(BASELINE, JSON.stringify({ eslint, stylelint }, null, 2) + '\n');
  console.log(`Lint baseline updated → eslint=${eslint}, stylelint=${stylelint}`);
  process.exit(0);
}

const base = JSON.parse(readFileSync(BASELINE, 'utf8'));
const fails = [];
if (eslint > base.eslint) fails.push(`ESLint errors rose to ${eslint} (baseline ${base.eslint}). Fix new issues — try \`npm run lint -- --fix\`.`);
if (stylelint > base.stylelint) fails.push(`stylelint errors rose to ${stylelint} (baseline ${base.stylelint}). Run \`npm run lint:style\`.`);

console.log('FHIRBridge lint ratchet');
console.log(`  eslint    : ${eslint} / ${base.eslint} baseline`);
console.log(`  stylelint : ${stylelint} / ${base.stylelint} baseline`);

if (fails.length) {
  console.error('\n✗ lint ratchet FAILED\n   - ' + fails.join('\n   - '));
  process.exit(1);
}
if (eslint < base.eslint || stylelint < base.stylelint) {
  console.log('\n  note: counts dropped below baseline — run `node scripts/lint-ratchet.mjs --update-baseline` and commit to lock it in.');
}
console.log('\n✓ lint ratchet passed');

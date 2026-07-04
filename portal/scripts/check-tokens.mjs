#!/usr/bin/env node
/**
 * FHIRBridge design-token compliance check (ratchet / no-regression).
 *
 * Enforces, over component stylesheets (src/app/**.scss):
 *   1. HARD RULE — components must not reference Tier-1 primitives (var(--ref-*)).
 *   2. RATCHET   — hardcoded hex count must not exceed the committed baseline.
 *   3. RATCHET   — !important count (app + styles.scss) must not exceed baseline.
 *
 * Ratchet = you may not ADD debt; as the cleanup lowers counts, re-baseline with
 *   `node scripts/check-tokens.mjs --update-baseline` and commit token-baseline.json.
 *
 * Exit 0 = pass, 1 = violation. No dependencies. See docs/design-token-architecture.md.
 */
import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve, relative } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const SRC = resolve(here, '..', 'src');
const APP = join(SRC, 'app');
const STYLES = join(SRC, 'styles.scss');
const BASELINE = join(here, 'token-baseline.json');

const HEX = /#[0-9a-fA-F]{3,8}\b/g;
const REF = /var\(--ref-/g;
const IMP = /!important/g;
const count = (txt, re) => (txt.match(re) || []).length;

function walk(dir, out = []) {
  for (const e of readdirSync(dir)) {
    const p = join(dir, e);
    if (statSync(p).isDirectory()) walk(p, out);
    else if (p.endsWith('.scss')) out.push(p);
  }
  return out;
}

const appFiles = walk(APP);
const styles = readFileSync(STYLES, 'utf8');

let hardcodedHex = 0;
let refUsages = 0;
let important = count(styles, IMP);
const refFiles = [];
for (const f of appFiles) {
  const txt = readFileSync(f, 'utf8');
  hardcodedHex += count(txt, HEX);
  const r = count(txt, REF);
  if (r) { refUsages += r; refFiles.push(relative(SRC, f)); }
  important += count(txt, IMP);
}

if (process.argv.includes('--update-baseline')) {
  writeFileSync(BASELINE, JSON.stringify({ componentHardcodedHex: hardcodedHex, important }, null, 2) + '\n');
  console.log(`Baseline updated → hardcodedHex=${hardcodedHex}, important=${important}`);
  process.exit(0);
}

const base = JSON.parse(readFileSync(BASELINE, 'utf8'));
const fails = [];
if (refUsages > 0) {
  fails.push(`Components reference Tier-1 primitives (var(--ref-*)) — use a semantic token (--color-*):\n     ${refFiles.join('\n     ')}`);
}
if (hardcodedHex > base.componentHardcodedHex) {
  fails.push(`Hardcoded hex in components rose to ${hardcodedHex} (baseline ${base.componentHardcodedHex}). Use var(--color-*)/tokens — don't add raw hex.`);
}
if (important > base.important) {
  fails.push(`!important rose to ${important} (baseline ${base.important}). Restructure selector specificity instead of adding !important.`);
}

console.log('FHIRBridge token compliance');
console.log(`  primitives in components : ${refUsages}  (must be 0)`);
console.log(`  hardcoded hex            : ${hardcodedHex} / ${base.componentHardcodedHex} baseline`);
console.log(`  !important               : ${important} / ${base.important} baseline`);

if (fails.length) {
  console.error('\n✗ token compliance FAILED\n   - ' + fails.join('\n   - '));
  process.exit(1);
}
if (hardcodedHex < base.componentHardcodedHex || important < base.important) {
  console.log('\n  note: counts dropped below baseline — run `node scripts/check-tokens.mjs --update-baseline` and commit to lock in the win.');
}
console.log('\n✓ token compliance passed');

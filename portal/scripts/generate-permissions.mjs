#!/usr/bin/env node
/**
 * FHIRBridge permission-name codegen.
 *
 * Generates src/app/auth/models/permission.constants.ts directly from the backend's own
 * enum source — src/FHIRBridge.Application/Rbac/{PermissionGroupCode,PermissionActionCode}.cs
 * — which is the actual single source of truth for these names (PermissionTaxonomy.
 * BuildPermissionCode lowercases the C# member name to build the wire-format string).
 * No network call and no running backend needed: frontend and backend live in the same
 * repo checkout, so this just reads the .cs files off disk.
 *
 * Regenerate after any backend group/action rename or addition:
 *   node scripts/generate-permissions.mjs
 *
 * Verify the committed file hasn't drifted from the backend (wired into portal-ci.yml):
 *   node scripts/generate-permissions.mjs --check
 *
 * Exit 0 = up to date / written, 1 = --check found drift. No dependencies.
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..', '..');
const RBAC_DIR = join(repoRoot, 'src', 'FHIRBridge.Application', 'Rbac');
const OUTPUT = join(here, '..', 'src', 'app', 'auth', 'models', 'permission.constants.ts');

function parseEnumMembers(filePath, enumName) {
  const source = readFileSync(filePath, 'utf8');
  const enumMatch = source.match(new RegExp(`enum\\s+${enumName}\\s*\\{([\\s\\S]*?)\\n\\}`));
  if (!enumMatch) {
    throw new Error(`generate-permissions: could not find "enum ${enumName}" in ${filePath}`);
  }

  const memberRe = /^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*\d+\s*,?\s*$/gm;
  const names = [];
  let match;
  while ((match = memberRe.exec(enumMatch[1])) !== null) names.push(match[1]);

  if (names.length === 0) {
    throw new Error(`generate-permissions: found 0 members in enum ${enumName} — parser or source may have changed shape`);
  }
  return names;
}

function renderEnum(tsName, members) {
  const lines = members.map(name => `  ${name} = '${name.toLowerCase()}',`);
  return `export enum ${tsName} {\n${lines.join('\n')}\n}`;
}

const groups = parseEnumMembers(join(RBAC_DIR, 'PermissionGroupCode.cs'), 'PermissionGroupCode');
const actions = parseEnumMembers(join(RBAC_DIR, 'PermissionActionCode.cs'), 'PermissionActionCode');

const generated = `// AUTO-GENERATED — do not hand-edit.
//
// Source of truth: src/FHIRBridge.Application/Rbac/PermissionGroupCode.cs and
// PermissionActionCode.cs on the backend (PermissionTaxonomy.BuildPermissionCode lowercases
// these same member names to build the wire-format code, e.g. "user.edit"). Regenerate after
// any group/action rename or addition there:
//   node scripts/generate-permissions.mjs
//
// A stale copy is caught in CI, not silently shipped — portal-ci.yml runs this same script
// with --check and fails the build if the committed file no longer matches the backend.

${renderEnum('PermissionGroup', groups)}

${renderEnum('PermissionAction', actions)}

/** Joins a group and action into the backend's wire-format permission code, e.g.
 *  \`permissionCode(PermissionGroup.User, PermissionAction.Edit)\` -> \`"user.edit"\`. */
export function permissionCode(group: PermissionGroup, action: PermissionAction): string {
  return \`\${group}.\${action}\`;
}
`;

if (process.argv.includes('--check')) {
  let existing = '';
  try {
    existing = readFileSync(OUTPUT, 'utf8');
  } catch {
    /* file doesn't exist yet — treated as drift below */
  }

  // Compare ignoring line-ending style: this repo has core.autocrlf=true, so a Windows
  // checkout of the committed (LF) file silently rewrites it to CRLF on disk — that's
  // not real drift, just a checkout artifact, and must not fail the build.
  const normalize = s => s.replace(/\r\n/g, '\n');
  if (normalize(existing) !== normalize(generated)) {
    console.error(
      'generate-permissions: permission.constants.ts is stale relative to the backend enums.\n' +
      'Run `node scripts/generate-permissions.mjs` locally and commit the result.'
    );
    process.exit(1);
  }
  console.log('generate-permissions: up to date.');
  process.exit(0);
}

writeFileSync(OUTPUT, generated);
console.log(`generate-permissions: wrote ${groups.length} groups, ${actions.length} actions -> ${OUTPUT}`);

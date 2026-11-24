// Regenerates libs/data-access/api-types/src/lib/generated.ts from the committed OpenAPI snapshot.
//
// There is exactly ONE committed OpenAPI document: docs/api/openapi.json, at the path
// src/backend/scripts/generate-openapi.sh writes (ADR-0004). Where it sits relative to this package
// depends on the repository this file is in, so it is looked up rather than hard-coded:
//   aj-boilerplate-fe   <repo>/docs/api/openapi.json       (the package IS the repository root)
//   aj-boilerplate-fs   <repo>/docs/api/openapi.json       (this package is <repo>/src/frontend)
// That keeps this file byte-identical in both repositories — scripts/derive.sh rewrites nothing here.
// The in-package candidate is tried first so a frontend clone is never matched by a stray document
// that happens to sit two directories above it.
import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const snapshot = ['docs/api/openapi.json', '../../docs/api/openapi.json']
  .map((p) => resolve(root, p))
  .find(existsSync);

if (!snapshot) {
  console.error(
    'generate-api: docs/api/openapi.json not found (looked beside this package and two levels up).',
  );
  process.exit(1);
}

const out = resolve(root, 'libs/data-access/api-types/src/lib/generated.ts');
const run = spawnSync(
  'npx',
  ['--no-install', 'openapi-typescript', snapshot, '-o', out],
  {
    cwd: root,
    stdio: 'inherit',
    shell: process.platform === 'win32',
  },
);
process.exit(run.status ?? 1);

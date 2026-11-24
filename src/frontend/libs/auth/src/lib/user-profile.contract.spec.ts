import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import type { UserProfile } from './auth.types';

// The committed API contract is `docs/api/openapi.json`. Its distance from this file differs between
// aj-boilerplate-fs (it is outside src/frontend) and aj-boilerplate-fe (it is inside the repository),
// so walk up to it instead of counting `../`. It is read from disk rather than imported, which also
// keeps the spec inside the module-boundary rule without an exemption.
function findOpenApiDocument(): string {
  for (
    let dir = dirname(fileURLToPath(import.meta.url));
    ;
    dir = dirname(dir)
  ) {
    const candidate = join(dir, 'docs', 'api', 'openapi.json');
    if (existsSync(candidate)) return candidate;
    if (dirname(dir) === dir)
      throw new Error(
        'docs/api/openapi.json not found above ' +
          dirname(fileURLToPath(import.meta.url)),
      );
  }
}
const openApiDocument: unknown = JSON.parse(
  readFileSync(findOpenApiDocument(), 'utf8'),
);

/**
 * `UserProfile` is hand-written (see its doc comment). This is what stops it drifting from the
 * contract: the committed OpenAPI document's `UserProfileResponse` must carry every field it names.
 * The record below must list every key of `UserProfile`, or the spec tsconfig refuses to compile.
 */
const profileFields: Record<keyof UserProfile, true> = {
  userId: true,
  displayName: true,
  email: true,
  roles: true,
  capabilities: true,
  permissions: true,
};

describe('UserProfile against the committed OpenAPI document', () => {
  const document = openApiDocument as {
    components: {
      schemas: Record<string, { properties?: Record<string, unknown> }>;
    };
  };
  const served = Object.keys(
    document.components.schemas['UserProfileResponse']?.properties ?? {},
  );

  it('reads a real schema (control: the document has properties to compare against)', () => {
    expect(served.length).toBeGreaterThan(3);
  });

  it('names no field the API does not serve', () => {
    expect(
      Object.keys(profileFields).filter((field) => !served.includes(field)),
    ).toEqual([]);
  });
});

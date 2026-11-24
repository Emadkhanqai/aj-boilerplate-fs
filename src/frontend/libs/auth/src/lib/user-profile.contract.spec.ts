import { describe, expect, it } from 'vitest';
// The committed API contract lives outside this workspace, on purpose: it is what the spec checks against.
// eslint-disable-next-line @nx/enforce-module-boundaries
import openApiDocument from '../../../../../../docs/api/openapi.json';
import type { UserProfile } from './auth.types';

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

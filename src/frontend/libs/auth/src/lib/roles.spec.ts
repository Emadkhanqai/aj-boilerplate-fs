import { describe, expect, it } from 'vitest';
import { NO_CAPABILITIES, ROLES, capabilitiesFor, capabilitiesForRoles, isRole } from './roles';

describe('roles', () => {
  it('recognises every declared role', () => {
    for (const role of ROLES) {
      expect(isRole(role)).toBe(true);
    }
  });

  it('rejects an unknown role string', () => {
    expect(isRole('superuser')).toBe(false);
  });

  it('grants a viewer read access only', () => {
    expect(capabilitiesFor('viewer')).toEqual({
      canView: true,
      canCreate: false,
      canEdit: false,
      canDelete: false,
      canAdminister: false,
    });
  });

  it('grants an admin every capability', () => {
    const caps = capabilitiesFor('admin');
    expect(Object.values(caps).every(Boolean)).toBe(true);
  });

  it('unions capabilities across multiple roles', () => {
    expect(capabilitiesForRoles(['viewer', 'editor'])).toEqual({
      canView: true,
      canCreate: true,
      canEdit: true,
      canDelete: false,
      canAdminister: false,
    });
  });

  it('returns the all-false baseline for no roles (fail safe)', () => {
    expect(capabilitiesForRoles([])).toEqual(NO_CAPABILITIES);
  });
});

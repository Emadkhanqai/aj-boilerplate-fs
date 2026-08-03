import { describe, expect, it } from 'vitest';
import {
  NO_CAPABILITIES,
  ROLES,
  capabilitiesFor,
  capabilitiesForRoles,
  isRole,
  toRole,
  toRoles,
} from './roles';

describe('roles', () => {
  it('recognises every declared role', () => {
    for (const role of ROLES) {
      expect(isRole(role)).toBe(true);
    }
  });

  it('rejects an unknown role string', () => {
    expect(isRole('superuser')).toBe(false);
  });

  describe('normalising role names off the wire', () => {
    it('accepts the canonical names the API actually returns', () => {
      // Regression: the API returns "Admin", `ROLES` holds "admin". An exact-match filter
      // discarded every role the server sent and the UI showed "no role" for real admins.
      expect(toRole('Admin')).toBe('admin');
      expect(toRole('Editor')).toBe('editor');
      expect(toRole('Viewer')).toBe('viewer');
    });

    it('tolerates the surrounding whitespace a claim can carry', () => {
      expect(toRole('  Admin ')).toBe('admin');
    });

    it('returns null for a role this application does not know', () => {
      expect(toRole('superuser')).toBeNull();
    });

    it('normalises a whole list, dropping only the unknown entries', () => {
      expect(toRoles(['Admin', 'superuser', 'VIEWER'])).toEqual(['admin', 'viewer']);
    });

    it('feeds values that resolve against the capability map', () => {
      // The point of returning a normalised value rather than widening the type guard: the
      // result has to be a usable key, not merely a string that type-checks.
      const roles = toRoles(['Admin']);
      expect(capabilitiesForRoles(roles).canAdminister).toBe(true);
    });
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

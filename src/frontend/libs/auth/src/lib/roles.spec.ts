import { describe, expect, it } from 'vitest';
import {
  NO_CAPABILITIES,
  ROLES,
  capabilitiesFor,
  capabilitiesForPermissions,
  capabilitiesForProfile,
  capabilitiesForRoles,
  highestRole,
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

describe('permissions -> capabilities', () => {
  it('maps items:read to view only', () => {
    expect(capabilitiesForPermissions(['items:read'])).toEqual({ ...NO_CAPABILITIES, canView: true });
  });

  it('maps items:write to view, create and edit but not delete or administer', () => {
    expect(capabilitiesForPermissions(['items:write'])).toEqual({
      canView: true,
      canCreate: true,
      canEdit: true,
      canDelete: false,
      canAdminister: false,
    });
  });

  it('maps admin:manage to every capability, the same set the admin role has', () => {
    expect(capabilitiesForPermissions(['admin:manage'])).toEqual(capabilitiesFor('admin'));
  });

  it('grants nothing for an unknown permission, and tolerates case and whitespace on a known one', () => {
    expect(capabilitiesForPermissions(['reports:read', 'items:delete'])).toEqual(NO_CAPABILITIES);
    expect(capabilitiesForPermissions([' Items:Read '])).toEqual({ ...NO_CAPABILITIES, canView: true });
  });

  it('is a union across several permissions', () => {
    const caps = capabilitiesForPermissions(['items:read', 'admin:manage']);
    expect(caps.canAdminister).toBe(true);
    expect(caps.canView).toBe(true);
  });
});

describe('capabilitiesForProfile', () => {
  const none = NO_CAPABILITIES;

  it('works for a profile with no permissions field at all (an API that predates it)', () => {
    expect(capabilitiesForProfile({ roles: ['Viewer'], capabilities: none })).toEqual(capabilitiesFor('viewer'));
  });

  it('lets a permission grant what the seat does not: seat OR permission', () => {
    const caps = capabilitiesForProfile({ roles: ['Viewer'], capabilities: none, permissions: ['items:write'] });
    expect(caps.canEdit).toBe(true);
    expect(caps.canDelete).toBe(false);
  });

  it('keeps what the server flags say even when roles and permissions grant nothing', () => {
    const caps = capabilitiesForProfile({
      roles: [],
      capabilities: { ...none, canDelete: true },
      permissions: [],
    });
    expect(caps).toEqual({ ...none, canDelete: true });
  });

  it('grants nothing to a profile with no roles, no permissions and no flags', () => {
    expect(capabilitiesForProfile({ roles: [] })).toEqual(NO_CAPABILITIES);
  });
});

describe('highestRole', () => {
  it('picks the highest seat whatever order the roles arrive in', () => {
    expect(highestRole(['editor', 'admin'])).toBe('admin');
    expect(highestRole(['admin', 'editor', 'viewer'])).toBe('admin');
    expect(highestRole(['viewer', 'editor'])).toBe('editor');
    expect(highestRole(['viewer'])).toBe('viewer');
  });

  it('is undefined when the user holds no role', () => {
    expect(highestRole([])).toBeUndefined();
  });
});

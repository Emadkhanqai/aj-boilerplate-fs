import { describe, expect, it } from 'vitest';
import { classifyRoleChange } from './role-change';
import { NO_CAPABILITIES, capabilitiesFor } from './roles';

describe('classifyRoleChange', () => {
  const viewer = capabilitiesFor('viewer');
  const editor = capabilitiesFor('editor');
  const admin = capabilitiesFor('admin');

  it('is a reduction when a capability was lost', () => {
    expect(classifyRoleChange(admin, editor)).toBe('reduction');
    expect(classifyRoleChange(viewer, NO_CAPABILITIES)).toBe('reduction');
  });

  it('is an increase when capabilities were only gained', () => {
    expect(classifyRoleChange(viewer, editor)).toBe('increase');
  });

  it('is a reduction when one was lost even though another was gained (the server already narrowed it)', () => {
    const swapped = { ...NO_CAPABILITIES, canView: true, canDelete: true };
    expect(classifyRoleChange({ ...NO_CAPABILITIES, canView: true, canEdit: true }, swapped)).toBe('reduction');
  });

  it('is unchanged for an identical set', () => {
    expect(classifyRoleChange(editor, { ...editor })).toBe('unchanged');
  });

  it('is unchanged when there was no earlier answer, because no honest diff exists', () => {
    expect(classifyRoleChange(null, admin)).toBe('unchanged');
  });
});

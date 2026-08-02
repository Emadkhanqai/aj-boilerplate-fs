import { beforeEach, describe, expect, it } from 'vitest';
import { DEV_SESSION_STORAGE_KEY, DEV_USERS, createDevProvider } from './dev-provider';

describe('devProvider', () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it('has no session before sign-in', () => {
    expect(createDevProvider().restore()).toBeNull();
  });

  it('persists the picked user so a reload keeps the session', async () => {
    const provider = createDevProvider();
    const session = await provider.signIn(DEV_USERS[0]?.userId);

    expect(session.roles).toEqual(['admin']);
    expect(window.localStorage.getItem(DEV_SESSION_STORAGE_KEY)).not.toBeNull();
    expect(createDevProvider().restore()?.user.userId).toBe(DEV_USERS[0]?.userId);
  });

  it('rejects an unknown user id', async () => {
    await expect(createDevProvider().signIn('nobody')).rejects.toThrow(/Unknown dev user/);
  });

  it('clears the persisted session on sign-out', async () => {
    const provider = createDevProvider();
    await provider.signIn(DEV_USERS[0]?.userId);
    provider.signOut();

    expect(provider.restore()).toBeNull();
  });

  it('ignores a tampered stored session with no valid role', () => {
    window.localStorage.setItem(
      DEV_SESSION_STORAGE_KEY,
      JSON.stringify({
        user: { userId: 'x', name: 'X', email: 'x@example.com' },
        roles: ['superuser'],
        accessToken: 'dev.x',
      }),
    );

    expect(createDevProvider().restore()).toBeNull();
  });

  it('synthesizes a profile whose capabilities match the picked role', async () => {
    const provider = createDevProvider();
    const session = await provider.signIn('u-viewer');
    const profile = await provider.fetchProfile(session);

    expect(profile.capabilities.canView).toBe(true);
    expect(profile.capabilities.canEdit).toBe(false);
  });
});

import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MessageService } from 'primeng/api';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthService, capabilitiesFor } from '@aj-boilerplate/auth';
import type { Capabilities, RoleRecheck } from '@aj-boilerplate/auth';
import {
  FORBIDDEN_CHECK_MIN_INTERVAL_MS,
  ROLE_RECHECK_RETRY_AFTER_MS,
  ROLES_VERSION_POLL_INTERVAL_MS,
  RoleChangeWatcher,
} from './role-change-watcher';

const admin = capabilitiesFor('admin');
const editor = capabilitiesFor('editor');
const viewer = capabilitiesFor('viewer');

describe('RoleChangeWatcher', () => {
  let confirmed: ReturnType<typeof signal<boolean>>;
  let recheck: ReturnType<typeof vi.fn<() => Promise<RoleRecheck | null>>>;
  let messages: { add: ReturnType<typeof vi.fn> };
  let fetchMock: ReturnType<typeof vi.fn>;
  let version: string | null;
  let visibility: DocumentVisibilityState;
  let mode: 'gateway' | 'dev';

  function watcher(): RoleChangeWatcher {
    return TestBed.inject(RoleChangeWatcher);
  }

  const outcome = (before: Capabilities | null, after: Capabilities): RoleRecheck => ({ before, after });

  /** Starts the watcher and lets the first poll run. */
  async function start(): Promise<RoleChangeWatcher> {
    const w = watcher();
    w.start();
    TestBed.tick();
    await vi.advanceTimersByTimeAsync(0);
    return w;
  }

  function setVisibility(state: DocumentVisibilityState): void {
    visibility = state;
    document.dispatchEvent(new Event('visibilitychange'));
  }

  beforeEach(() => {
    vi.useFakeTimers();
    confirmed = signal(true);
    mode = 'gateway';
    version = 'v1';
    visibility = 'visible';
    recheck = vi.fn<() => Promise<RoleRecheck | null>>().mockResolvedValue(outcome(admin, admin));
    messages = { add: vi.fn() };
    fetchMock = vi.fn(async () => ({ ok: true, json: async () => ({ rolesVersion: version }) }));
    vi.stubGlobal('fetch', fetchMock);
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => visibility });
    TestBed.configureTestingModule({
      providers: [
        { provide: MessageService, useValue: messages },
        {
          provide: AuthService,
          useValue: {
            get mode() {
              return mode;
            },
            sessionConfirmed: confirmed,
            onSignInPage: () => false,
            recheckCapabilities: recheck,
          },
        },
      ],
    });
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    delete (document as unknown as Record<string, unknown>)['visibilityState'];
  });

  describe('reacting to a change (observe)', () => {
    it('treats the FIRST answer as the baseline, not a change', () => {
      const w = watcher();

      w.observe('v1');

      expect(recheck).not.toHaveBeenCalled();
      expect(w.blocking()).toBe(false);
    });

    it('FEWER capabilities: blocks with the reload dialog and shows no toast', async () => {
      const w = watcher();
      recheck.mockResolvedValue(outcome(admin, viewer));
      w.observe('v1');

      w.observe('v2');
      await vi.advanceTimersByTimeAsync(0);

      expect(recheck).toHaveBeenCalledTimes(1);
      expect(w.blocking()).toBe(true);
      expect(messages.add).not.toHaveBeenCalled();
    });

    it('MORE capabilities: a toast, and no blocking dialog', async () => {
      const w = watcher();
      recheck.mockResolvedValue(outcome(viewer, editor));
      w.observe('v1');

      w.observe('v2');
      await vi.advanceTimersByTimeAsync(0);

      expect(w.blocking()).toBe(false);
      expect(messages.add).toHaveBeenCalledTimes(1);
      expect(messages.add.mock.calls[0]?.[0]).toMatchObject({ severity: 'info', summary: 'Your access has changed' });
    });

    it('UNCHANGED capabilities behind a moved fingerprint: nothing shown, and the new version is not re-checked', async () => {
      const w = watcher();
      recheck.mockResolvedValue(outcome(editor, { ...editor }));
      w.observe('v1');

      w.observe('v2');
      await vi.advanceTimersByTimeAsync(0);
      w.observe('v2');
      await vi.advanceTimersByTimeAsync(0);

      expect(recheck).toHaveBeenCalledTimes(1);
      expect(w.blocking()).toBe(false);
      expect(messages.add).not.toHaveBeenCalled();
    });

    it('does not re-check the same version twice', async () => {
      const w = watcher();
      w.observe('v1');

      w.observe('v1');
      w.observe('v1');

      expect(recheck).not.toHaveBeenCalled();
    });

    it('when the profile cannot be re-read, keeps the baseline and retries the SAME change after the back-off', async () => {
      const w = watcher();
      recheck.mockResolvedValueOnce(null).mockResolvedValue(outcome(admin, viewer));
      w.observe('v1');

      w.observe('v2');
      await vi.advanceTimersByTimeAsync(0);
      w.observe('v2');
      expect(recheck).toHaveBeenCalledTimes(1);
      expect(w.blocking()).toBe(false);

      await vi.advanceTimersByTimeAsync(ROLE_RECHECK_RETRY_AFTER_MS);
      w.observe('v2');
      await vi.advanceTimersByTimeAsync(0);

      expect(recheck).toHaveBeenCalledTimes(2);
      expect(w.blocking()).toBe(true);
    });

    it('is inert once the blocking dialog is up', async () => {
      const w = watcher();
      recheck.mockResolvedValue(outcome(admin, viewer));
      w.observe('v1');
      w.observe('v2');
      await vi.advanceTimersByTimeAsync(0);

      w.observe('v3');

      expect(recheck).toHaveBeenCalledTimes(1);
    });
  });

  describe('polling /auth/roles-version', () => {
    it('polls straight away, then every 15 s while the tab is visible', async () => {
      await start();
      expect(fetchMock).toHaveBeenCalledTimes(1);
      expect(fetchMock.mock.calls[0]?.[0]).toBe('/auth/roles-version');
      expect(fetchMock.mock.calls[0]?.[1]).toMatchObject({ credentials: 'same-origin', cache: 'no-store' });

      await vi.advanceTimersByTimeAsync(ROLES_VERSION_POLL_INTERVAL_MS);
      await vi.advanceTimersByTimeAsync(ROLES_VERSION_POLL_INTERVAL_MS);

      expect(fetchMock).toHaveBeenCalledTimes(3);
    });

    it('PAUSES while the tab is hidden, and polls again the moment it is visible', async () => {
      await start();
      setVisibility('hidden');
      fetchMock.mockClear();

      await vi.advanceTimersByTimeAsync(ROLES_VERSION_POLL_INTERVAL_MS * 4);
      expect(fetchMock).not.toHaveBeenCalled();

      setVisibility('visible');
      await vi.advanceTimersByTimeAsync(0);
      expect(fetchMock).toHaveBeenCalledTimes(1);

      await vi.advanceTimersByTimeAsync(ROLES_VERSION_POLL_INTERVAL_MS);
      expect(fetchMock).toHaveBeenCalledTimes(2);
    });

    it('end to end: a changed fingerprint from the endpoint blocks when capabilities fell', async () => {
      recheck.mockResolvedValue(outcome(admin, viewer));
      const w = await start(); // baseline v1
      version = 'v2';

      await vi.advanceTimersByTimeAsync(ROLES_VERSION_POLL_INTERVAL_MS);

      expect(recheck).toHaveBeenCalledTimes(1);
      expect(w.blocking()).toBe(true);
    });

    it.each([
      ['a non-ok answer', { ok: false, json: async () => ({ rolesVersion: 'v9' }) }],
      ['a null fingerprint (no roles on the session)', { ok: true, json: async () => ({ rolesVersion: null }) }],
    ])('ignores %s', async (_label, response) => {
      const w = await start(); // baseline v1
      fetchMock.mockResolvedValue(response);

      await vi.advanceTimersByTimeAsync(ROLES_VERSION_POLL_INTERVAL_MS);

      expect(recheck).not.toHaveBeenCalled();
      expect(w.blocking()).toBe(false);
    });

    it('survives a network error and keeps polling', async () => {
      await start();
      fetchMock.mockRejectedValueOnce(new Error('offline'));

      await vi.advanceTimersByTimeAsync(ROLES_VERSION_POLL_INTERVAL_MS);
      await vi.advanceTimersByTimeAsync(ROLES_VERSION_POLL_INTERVAL_MS);

      expect(fetchMock).toHaveBeenCalledTimes(3);
    });

    it('does not start until a session is confirmed, and starts once one is', async () => {
      confirmed.set(false);
      await start();
      expect(fetchMock).not.toHaveBeenCalled();

      confirmed.set(true);
      TestBed.tick();
      await vi.advanceTimersByTimeAsync(0);

      expect(fetchMock).toHaveBeenCalledTimes(1);
    });

    it('does nothing in dev mode: there is no server session to watch', async () => {
      mode = 'dev';
      await start();

      await vi.advanceTimersByTimeAsync(ROLES_VERSION_POLL_INTERVAL_MS * 2);

      expect(fetchMock).not.toHaveBeenCalled();
    });
  });

  describe('after a 403 (checkNow)', () => {
    it('polls immediately instead of waiting for the next tick', async () => {
      const w = await start();
      fetchMock.mockClear();

      w.checkNow();
      await vi.advanceTimersByTimeAsync(0);

      expect(fetchMock).toHaveBeenCalledTimes(1);
    });

    it('is throttled: a burst of 403s causes one check, and another is allowed after the interval', async () => {
      const w = await start();
      fetchMock.mockClear();

      w.checkNow();
      w.checkNow();
      w.checkNow();
      await vi.advanceTimersByTimeAsync(0);
      expect(fetchMock).toHaveBeenCalledTimes(1);

      await vi.advanceTimersByTimeAsync(FORBIDDEN_CHECK_MIN_INTERVAL_MS);
      fetchMock.mockClear();
      w.checkNow();
      await vi.advanceTimersByTimeAsync(0);

      expect(fetchMock).toHaveBeenCalledTimes(1);
    });

    it('does nothing before the watcher is listening', async () => {
      watcher().checkNow();
      await vi.advanceTimersByTimeAsync(0);

      expect(fetchMock).not.toHaveBeenCalled();
    });
  });
});

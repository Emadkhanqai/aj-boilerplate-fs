import { describe, expect, it, vi } from 'vitest';
import { installChunkLoadErrorReload, isChunkLoadErrorMessage } from './chunk-load-error-handler';

describe('isChunkLoadErrorMessage', () => {
  it('matches the Vite/browser "Failed to fetch dynamically imported module" phrasing', () => {
    expect(isChunkLoadErrorMessage('TypeError: Failed to fetch dynamically imported module: https://x/chunk-ABC.js')).toBe(true);
  });

  it('matches the Vite "error loading dynamically imported module" phrasing', () => {
    expect(isChunkLoadErrorMessage('error loading dynamically imported module')).toBe(true);
  });

  it('does not match an unrelated error message', () => {
    expect(isChunkLoadErrorMessage('TypeError: Cannot read properties of undefined')).toBe(false);
  });

  it('does not match null/undefined/empty messages', () => {
    expect(isChunkLoadErrorMessage(null)).toBe(false);
    expect(isChunkLoadErrorMessage(undefined)).toBe(false);
    expect(isChunkLoadErrorMessage('')).toBe(false);
  });
});

/** Minimal fake of the DOM event target surface `installChunkLoadErrorReload` depends on, so the
 * test can synthesize `unhandledrejection`/`error` events without a real browser. */
class FakeEventTarget {
  private readonly listeners = new Map<string, ((event: unknown) => void)[]>();

  addEventListener(type: string, listener: (event: unknown) => void): void {
    const existing = this.listeners.get(type) ?? [];
    existing.push(listener);
    this.listeners.set(type, existing);
  }

  dispatch(type: string, event: unknown): void {
    for (const listener of this.listeners.get(type) ?? []) {
      listener(event);
    }
  }
}

function fakeStorage(): Storage {
  const map = new Map<string, string>();
  return {
    getItem: (key: string) => map.get(key) ?? null,
    setItem: (key: string, value: string) => {
      map.set(key, value);
    },
    removeItem: (key: string) => {
      map.delete(key);
    },
    clear: () => map.clear(),
    key: () => null,
    length: 0,
  } as Storage;
}

describe('installChunkLoadErrorReload', () => {
  it('reloads exactly once when an unhandledrejection carries the chunk-load error message', () => {
    const target = new FakeEventTarget();
    const storage = fakeStorage();
    const reload = vi.fn();

    installChunkLoadErrorReload(target as unknown as Window, storage, reload);

    target.dispatch('unhandledrejection', {
      reason: new Error('Failed to fetch dynamically imported module: https://x/chunk-ABC.js'),
    });

    expect(reload).toHaveBeenCalledTimes(1);
  });

  // Regression test for a real live-QA incident (2026-07-28): two deploys landed minutes apart
  // while a QA tab stayed open. The first chunk-load failure triggered a reload (correct — it
  // re-fetched the then-current index.html). The SECOND deploy then made a DIFFERENT chunk stale
  // again, but the old one-shot-forever flag refused to reload a second time, so the user was
  // left staring at a raw console error with no recovery for the rest of that tab session. A
  // reload must be allowed again once enough time has passed since the last one — only truly
  // back-to-back failures (the same still-broken deploy) should be suppressed.
  it('reloads again for a second chunk-load failure once the cooldown window has passed', () => {
    const target = new FakeEventTarget();
    const storage = fakeStorage();
    const reload = vi.fn();
    let now = 0;

    installChunkLoadErrorReload(target as unknown as Window, storage, reload, () => now);

    target.dispatch('unhandledrejection', {
      reason: new Error('Failed to fetch dynamically imported module: https://x/chunk-ABC.js'),
    });
    expect(reload).toHaveBeenCalledTimes(1);

    // A second deploy landed 5 minutes later, making a different chunk stale.
    now += 5 * 60_000;
    target.dispatch('unhandledrejection', {
      reason: new Error('Failed to fetch dynamically imported module: https://x/chunk-XYZ.js'),
    });

    expect(reload).toHaveBeenCalledTimes(2);
  });

  it('does NOT reload again for a chunk-load failure that fires immediately after the last reload (tight-loop protection)', () => {
    const target = new FakeEventTarget();
    const storage = fakeStorage();
    const reload = vi.fn();
    let now = 0;

    installChunkLoadErrorReload(target as unknown as Window, storage, reload, () => now);

    target.dispatch('unhandledrejection', {
      reason: new Error('Failed to fetch dynamically imported module: https://x/chunk-ABC.js'),
    });
    expect(reload).toHaveBeenCalledTimes(1);

    // Same still-broken deploy fails again a second later — must not loop.
    now += 1000;
    target.dispatch('unhandledrejection', {
      reason: new Error('Failed to fetch dynamically imported module: https://x/chunk-ABC.js'),
    });

    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('does not reload for an unrelated unhandledrejection', () => {
    const target = new FakeEventTarget();
    const storage = fakeStorage();
    const reload = vi.fn();

    installChunkLoadErrorReload(target as unknown as Window, storage, reload);

    target.dispatch('unhandledrejection', { reason: new Error('Network request failed') });

    expect(reload).not.toHaveBeenCalled();
  });

  it('reloads at most once even if the chunk-load error fires repeatedly (no reload loop)', () => {
    const target = new FakeEventTarget();
    const storage = fakeStorage();
    const reload = vi.fn();

    installChunkLoadErrorReload(target as unknown as Window, storage, reload);

    target.dispatch('unhandledrejection', {
      reason: new Error('Failed to fetch dynamically imported module: https://x/chunk-ABC.js'),
    });
    target.dispatch('unhandledrejection', {
      reason: new Error('Failed to fetch dynamically imported module: https://x/chunk-ABC.js'),
    });
    target.dispatch('error', { message: 'Failed to fetch dynamically imported module: https://x/chunk-ABC.js' });

    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('reloads on a plain ErrorEvent carrying the chunk-load error message', () => {
    const target = new FakeEventTarget();
    const storage = fakeStorage();
    const reload = vi.fn();

    installChunkLoadErrorReload(target as unknown as Window, storage, reload);

    target.dispatch('error', { message: 'Failed to fetch dynamically imported module: https://x/chunk-XYZ.js' });

    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('does not reload again if a reload already happened within the cooldown window', () => {
    const target = new FakeEventTarget();
    const storage = fakeStorage();
    storage.setItem('app:chunk-load-error-reloaded-at', '0');
    const reload = vi.fn();

    installChunkLoadErrorReload(target as unknown as Window, storage, reload, () => 1000);

    target.dispatch('unhandledrejection', {
      reason: new Error('Failed to fetch dynamically imported module: https://x/chunk-ABC.js'),
    });

    expect(reload).not.toHaveBeenCalled();
  });
});

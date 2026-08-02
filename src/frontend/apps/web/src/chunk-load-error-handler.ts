/**
 * Detects the well-known Angular/Vite lazy-chunk-loading failure that happens when a browser tab
 * stays open across a deploy: the tab's `index.html` (and its in-memory router config) still
 * references chunk hashes that a NEWER deploy has since replaced on the server, so a lazy
 * `import()` (any `loadComponent` route — see `app.routes.ts`) 404s
 * with a message like `Failed to fetch dynamically imported module:
 * https://.../chunk-XXXX.js`. Vite's other phrasing for the same failure is `error loading
 * dynamically imported module`. There is no way to recover the specific missing chunk from
 * inside the stale tab — the only fix is a full page reload, which re-fetches the CURRENT
 * `index.html` (and therefore the current chunk hashes). Exported separately from
 * `installChunkLoadErrorReload` so the detection logic is unit-testable without touching
 * `window`/`sessionStorage`.
 */
export function isChunkLoadErrorMessage(message: string | null | undefined): boolean {
  if (message === null || message === undefined || message === '') {
    return false;
  }
  return message.includes('Failed to fetch dynamically imported module') || message.includes('error loading dynamically imported module');
}

/** `sessionStorage` key holding the timestamp (ms, from the injected `now`) of the last
 * auto-reload, used to gate further reloads — see `RELOAD_COOLDOWN_MS`. */
const RELOAD_TIMESTAMP_KEY = 'app:chunk-load-error-reloaded-at';

/** Minimum time between auto-reloads. Short enough that a second, genuinely different deploy
 * (a new stale chunk, minutes or hours later, in a tab that's stayed open) still triggers a
 * recovering reload; long enough that a single still-broken deploy — the SAME chunk 404ing
 * again immediately after the reload — can't loop the reload forever. A one-shot-forever flag is
 * the tempting simpler design and it is wrong: it recovers from the first deploy, then
 * permanently refuses to recover from a second one for the rest of that tab's life, leaving the
 * user stuck looking at a raw console error. */
export const RELOAD_COOLDOWN_MS = 10_000;

/**
 * Installs global `error`/`unhandledrejection` listeners that detect the stale-chunk failure
 * described above and trigger a full page reload, at most once per `RELOAD_COOLDOWN_MS` window.
 * A failed dynamic `import()` surfaces as an unhandled promise rejection in every case observed
 * in this app (Angular Router's lazy route loading, and any other ad-hoc `import()`); the `error`
 * listener is a defensive second path for the rarer case where a script-tag-style load failure
 * fires a plain `ErrorEvent` instead.
 *
 * `target`/`storage`/`reload`/`now` are injectable purely for unit testing — production code
 * always calls this with no arguments (see `main.ts`).
 */
export function installChunkLoadErrorReload(
  target: Pick<Window, 'addEventListener'> = window,
  storage: Pick<Storage, 'getItem' | 'setItem'> = sessionStorage,
  reload: () => void = () => window.location.reload(),
  now: () => number = () => Date.now(),
): void {
  const handleMessage = (message: string | null | undefined): void => {
    if (!isChunkLoadErrorMessage(message)) {
      return;
    }
    const storedTimestamp = storage.getItem(RELOAD_TIMESTAMP_KEY);
    const lastReloadAt = storedTimestamp === null ? null : Number(storedTimestamp);
    if (lastReloadAt !== null && now() - lastReloadAt < RELOAD_COOLDOWN_MS) {
      // Reloaded very recently — a further attempt this soon would just loop against the same
      // still-broken deploy, rather than recover from a later, different one.
      return;
    }
    storage.setItem(RELOAD_TIMESTAMP_KEY, String(now()));
    reload();
  };

  target.addEventListener('unhandledrejection', (event) => {
    const reason: unknown = event.reason;
    const message = reason instanceof Error ? reason.message : typeof reason === 'string' ? reason : undefined;
    handleMessage(message);
  });

  target.addEventListener('error', (event) => {
    const err: unknown = event.error;
    const message = err instanceof Error ? err.message : event.message;
    handleMessage(message);
  });
}

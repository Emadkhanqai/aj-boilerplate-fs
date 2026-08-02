import { InjectionToken } from '@angular/core';

/**
 * Attempts to obtain a fresh bearer token for the current session (e.g. via the OIDC
 * refresh-token grant) and returns it, or `null` if refreshing isn't possible/failed. Bound by
 * `AuthService` (`libs/auth`) — mirrors `AUTH_TOKEN_PROVIDER`'s pattern of keeping the API client
 * decoupled from the auth layer (`scope:data-access` may not depend on `scope:auth`). Optional:
 * providers that don't support refresh (dev mode, or a session with no `offline_access` refresh
 * token) simply don't provide this token, or resolve `null`.
 */
export const TOKEN_REFRESHER = new InjectionToken<() => Promise<string | null>>('TOKEN_REFRESHER');

/**
 * Notified once, from `authInterceptor`, the moment a request that carried a bearer token comes
 * back `401` and no refresh was possible — i.e. the session is no longer valid server-side
 * (expired/revoked). Bound by `AuthService` to clear the session and flip a `sessionExpired` UX
 * flag; kept as its own token (rather than importing `AuthService` directly) for the same
 * layering reason as `TOKEN_REFRESHER`.
 */
export const SESSION_EXPIRED_NOTIFIER = new InjectionToken<() => void>('SESSION_EXPIRED_NOTIFIER');

/**
 * Tracks which thrown errors already drove a `SESSION_EXPIRED_NOTIFIER` call. `authInterceptor`
 * marks an error right before rethrowing it from any of its three unrecoverable-401 branches;
 * any later global error surface (e.g. the TanStack Query error toasts wired in
 * `apps/web/src/app/app.config.ts`) can then check `isSessionExpiredError` and skip its own
 * notification — the login page's "session expired" message is enough, a generic error toast
 * on top of it would just be noise. A `WeakSet` keyed by the error object itself: no mutation
 * of `ApiError`, no leak (entries drop once the error itself is garbage-collected).
 */
const sessionExpiredErrors = new WeakSet<object>();

/** Called by `authInterceptor` alongside `SESSION_EXPIRED_NOTIFIER` — marks `err` so a global
 * error handler downstream can recognize it was already surfaced via the session-expired flow. */
export function markSessionExpired(err: unknown): void {
  if (typeof err === 'object' && err !== null) {
    sessionExpiredErrors.add(err);
  }
}

/** True when `err` was previously passed to `markSessionExpired` — i.e. it's a 401 that already
 * drove the session-expired UX and should not also produce a generic error toast. */
export function isSessionExpiredError(err: unknown): boolean {
  return typeof err === 'object' && err !== null && sessionExpiredErrors.has(err);
}

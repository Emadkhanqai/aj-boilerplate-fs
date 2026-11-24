import { InjectionToken } from '@angular/core';

/**
 * Notified once, from `authInterceptor`, when a request on a live session comes back `401` — the
 * session ended server-side (idle or absolute lifetime, or the identity provider refused a
 * refresh). Bound by `AuthService` (`libs/auth`) to clear the session and flip a `sessionExpired`
 * flag the shell turns into a redirect to `/login`. A DI token rather than an import, because the
 * module-boundary rule forbids `scope:data-access` from depending on `scope:auth`.
 */
export const SESSION_EXPIRED_NOTIFIER = new InjectionToken<() => void>('SESSION_EXPIRED_NOTIFIER');

/**
 * Whether the app holds a session whose credential it cannot see (the gateway's HttpOnly cookie).
 * Bound by `AuthService`. A 401 carries no token to inspect, so this is what tells "this session
 * ended" apart from "an anonymous visitor on the sign-in page was refused".
 */
export const AMBIENT_SESSION_ACTIVE = new InjectionToken<() => boolean>('AMBIENT_SESSION_ACTIVE');

/**
 * Errors that were ALREADY surfaced to the user through a dedicated flow (the session-expired
 * redirect, the access-revoked dialog). Global error surfaces (the TanStack error toasts, the
 * global error handler) check {@link isSessionExpiredError} and stay quiet: a generic
 * "Something went wrong" on top would be noise. A `WeakSet` keyed by the error object, so there is
 * no mutation of `ApiError` and no leak.
 */
const sessionExpiredErrors = new WeakSet<object>();

/** Marks `err` as already surfaced by a dedicated flow. */
export function markSessionExpired(err: unknown): void {
  if (typeof err === 'object' && err !== null) {
    sessionExpiredErrors.add(err);
  }
}

/** True when `err` was marked by {@link markSessionExpired}. */
export function isSessionExpiredError(err: unknown): boolean {
  return typeof err === 'object' && err !== null && sessionExpiredErrors.has(err);
}

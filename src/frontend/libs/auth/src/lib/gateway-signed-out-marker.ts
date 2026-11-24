/**
 * "This browser signed out" — the one fact that lets the next sign-in ask the identity provider to
 * challenge again (`prompt=login`) instead of readmitting the user off its still-live SSO cookie.
 * Without it, signing out and pressing Back silently undoes the sign-out: the gateway answers the
 * Back navigation with the same `302 /login` it sends any unauthenticated visitor.
 *
 * `localStorage`, not `sessionStorage`: per ORIGIN, so one sign-out is seen by every tab. The
 * marker is CONSUMED when read, so the cost is one challenge per sign-out, not one per arrival.
 * It does not reach another browser, profile or device.
 *
 * Every read and write fails SAFE, and "safe" points toward NO prompt: if storage cannot prove this
 * browser signed out, the user is readmitted silently rather than challenged in every private
 * window.
 */
const MARKER_KEY = 'app.auth.gateway-signed-out';

/** Records that a sign-out happened. Called before `POST /auth/logout`, so a hung request still counts. */
export function markSignedOut(): void {
  try {
    globalThis.localStorage?.setItem(MARKER_KEY, '1');
  } catch {
    // Storage unavailable. The sign-out itself must not fail for this.
  }
}

/** Whether a sign-out happened in this browser — and CLEARS the marker as it reads. */
export function consumeSignedOut(): boolean {
  let signedOut = false;
  try {
    // `=== '1'`, not `!== null`: an absent `localStorage` yields `undefined`, and
    // `undefined !== null` would report "signed out" on every storage-less environment.
    signedOut = globalThis.localStorage?.getItem(MARKER_KEY) === '1';
  } catch {
    return false;
  }
  try {
    globalThis.localStorage?.removeItem(MARKER_KEY);
  } catch {
    // Could not clear it: it survives and challenges once more — the over-cautious direction.
  }
  return signedOut;
}

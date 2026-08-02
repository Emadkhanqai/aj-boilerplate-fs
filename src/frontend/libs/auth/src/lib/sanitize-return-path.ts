/**
 * Constrains a post-login `?from=` redirect target to a same-origin, in-app path.
 * `oidc-provider.ts` captures `from` off the (unauthenticated) query string before handing the
 * browser off to the identity provider, and `auth-callback-page.ts` / `login-page.ts` later feed
 * the round-tripped value into `window.location.assign()` / `Router.navigateByUrl()`. Without
 * this guard, `?from=https://evil.example` or a protocol-relative `?from=//evil.example` would
 * navigate the browser off-origin immediately after a successful sign-in — a classic
 * open-redirect.
 *
 * This deliberately delegates to the WHATWG `URL` parser rather than hand-rolling string prefix
 * checks: `window.location.assign()` (the actual sink) uses that same parser under the hood, and
 * it does things naive string checks don't anticipate — e.g. treating a leading `\` as `/` in
 * authority position, and stripping tab/LF/CR before parsing. That gap is exactly what lets
 * `/\evil.example/phish`, `/<TAB>/evil.example`, and `/<LF>/evil.example` slip past a
 * `startsWith('/')` check while still resolving off-origin at the sink. Parsing `from` with the
 * same algorithm the sink uses, then checking the *resolved* origin, closes that gap by
 * construction instead of chasing each new bypass string.
 *
 * Same-origin paths additionally have OAuth/OIDC protocol parameters stripped (see
 * `AUTH_PROTOCOL_PARAMS`). Identity providers routinely echo their own `state` back onto the
 * post-logout landing URL, so the browser arrives at `/?state=eyJpZCI6…`; `authGuard` then
 * captures the whole serialized URL as `from`, the provider stashes it, and the callback page
 * replays it — so the junk survives every sign-out/sign-in cycle and compounds
 * (`?from=%2F%3Fstate%3D…%253D`). Dropping the protocol params here fixes the guard capture, the
 * login-page navigate, and the callback assign at once, while legitimate application query
 * params (`?tab=1`) are preserved. It also stops a stale `code`/`id_token` from being replayed
 * into the app's own URL bar after authentication.
 */
const AUTH_PROTOCOL_PARAMS = [
  'state',
  'code',
  'session_state',
  'error',
  'error_description',
  'error_uri',
  'client_info',
  'id_token',
] as const;

export function sanitizeReturnPath(from: string): string {
  try {
    const url = new URL(from, window.location.origin);
    if (url.origin !== window.location.origin) {
      return '/';
    }
    for (const param of AUTH_PROTOCOL_PARAMS) {
      url.searchParams.delete(param);
    }
    return `${url.pathname}${url.search}${url.hash}`;
  } catch {
    return '/';
  }
}

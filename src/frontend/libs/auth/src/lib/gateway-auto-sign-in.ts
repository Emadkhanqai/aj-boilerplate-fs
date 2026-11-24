/**
 * The one-shot claim that lets `/login` start a gateway sign-in WITHOUT a click.
 *
 * The gateway answers an unauthenticated navigation with `302 /login?from=<path>`, so the login
 * page loads and may invoke `signIn()` itself. That needs a brake: if `/auth/validate` keeps
 * answering 401 after an apparently successful sign-in (a cookie that will not set, clock skew),
 * the browser would cycle gateway -> IdP -> `/login` forever. The login screen waiting for a human
 * is what normally breaks that cycle; auto-invoking removes it, so this reinstates it.
 *
 * `sessionStorage`, because the IdP hop is a real full-document navigation: component state and
 * module variables die with it, `sessionStorage` survives a same-tab round trip. A claim is spent
 * after {@link GATEWAY_AUTO_SIGN_IN_WINDOW_MS} (longer than an interactive sign-in, far longer than
 * the sub-second cycle a broken session produces). It fails SAFE: any unreadable or unwritable
 * storage returns `false` ("do not auto-invoke"), costing one click, never a loop.
 */
const CLAIM_KEY = 'app.auth.gateway-auto-sign-in';

export const GATEWAY_AUTO_SIGN_IN_WINDOW_MS = 5 * 60 * 1000;

/** Claims the right to ONE automatic sign-in per window per tab; records the claim before returning. */
export function claimGatewayAutoSignIn(now: number = Date.now()): boolean {
  let storage: Storage | null | undefined;
  try {
    storage = globalThis.sessionStorage;
  } catch {
    return false;
  }
  if (storage === null || storage === undefined) {
    return false;
  }

  let previous: string | null;
  try {
    previous = storage.getItem(CLAIM_KEY);
  } catch {
    return false;
  }

  if (previous !== null) {
    const stamp = Number(previous);
    // Unparseable content and a stamp in the future (a clock that moved backwards) both count as
    // a LIVE claim: the cheap direction to be wrong in is "ask the user to click".
    const spent = Number.isFinite(stamp) && now - stamp >= GATEWAY_AUTO_SIGN_IN_WINDOW_MS;
    if (!spent) {
      return false;
    }
  }

  try {
    storage.setItem(CLAIM_KEY, String(now));
  } catch {
    return false;
  }
  return true;
}

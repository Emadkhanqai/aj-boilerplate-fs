import { inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type { AuthProviderApi, AuthSession, UserProfile } from '../auth.types';
import { sanitizeReturnPath } from '../sanitize-return-path';
import { loginHintFor } from '../login-hint';
import { consumeSignedOut, markSignedOut } from '../gateway-signed-out-marker';

/**
 * Gateway mode — the browser performs NO identity-provider interaction and holds NO token.
 * nginx validates the opaque HttpOnly session cookie with the Auth service and injects the
 * Keycloak JWT on the way to the API (docs/auth/gateway-contract.md).
 *
 * - `signIn()` is a FULL-PAGE navigation to `/auth/login?rd=<from>`. It must not be a `Router`
 *   navigation: a client-side route never reaches nginx, so the redirect to the identity provider
 *   would never happen and the button would appear to do nothing.
 * - The query-parameter names are deliberate and must not be unified. `from` is the name on the
 *   `/login` hop (the gateway supplies it; `sanitizeReturnPath` constrains it). `rd` is the name
 *   on the `/auth/login` hop, where the Auth service has its own guard. Two hops, two guards.
 * - `signOut()` POSTs `/auth/logout` (same-origin, so the Auth service accepts it) and goes to the
 *   static `/logged-out` page. It ends THIS session only; the SSO session at the identity provider
 *   is untouched, which is why it also marks the browser so the next sign-in sends `prompt=login`.
 * - The server stays the only source of identity and capabilities (`GET /api/v1/me`).
 *
 * Never select this mode for a deployment that is not behind the gateway: `/auth/login` is served
 * by the gateway, so elsewhere the sign-in control would navigate to a path that does not exist.
 */

/**
 * Full-page navigation seam: real code delegates to `globalThis.location.assign`. Tests replace
 * `navigate` instead of spying on `location.assign`, which jsdom makes non-configurable.
 */
export const gatewayNavigation = {
  navigate(url: string): void {
    globalThis.location.assign(url);
  },
};

export function createGatewayProvider(): AuthProviderApi {
  const http = inject(HttpClient);

  /** The placeholder principal shown until `GET /api/v1/me` resolves. It grants nothing. */
  const gatewaySession = (): AuthSession => ({ user: { userId: '', name: '', email: '' }, roles: [] });

  return {
    mode: 'gateway',
    devUsers: null,
    ambientCredential: true,

    /** Always a session: the gateway would not have served the app shell without one. */
    restore: () => gatewaySession(),

    signIn: (_userId?: string, loginHint?: string): Promise<AuthSession> => {
      // `from` is read off `location.search`, not `ActivatedRoute`: this provider is built at
      // start-up, before the router has resolved a route.
      const from = sanitizeReturnPath(new URLSearchParams(globalThis.location.search).get('from') ?? '/');
      // `prompt=login` only when this browser signed out; consumed here so one sign-out costs one
      // challenge. Omitted, never sent empty: `prompt=` is a different request from no `prompt`.
      const prompt = consumeSignedOut() ? '&prompt=login' : '';
      // `login_hint` is appended LAST so the rest of the URL is unchanged when there is none.
      const hint = loginHintFor(loginHint);
      const hintParam = hint === null ? '' : `&login_hint=${encodeURIComponent(hint)}`;
      gatewayNavigation.navigate(`/auth/login?rd=${encodeURIComponent(from)}${prompt}${hintParam}`);
      // The browser is navigating away: never settle, so the login page's own `router.navigateByUrl`
      // cannot race and cancel the full-page navigation.
      return new Promise<AuthSession>(() => {
        /* never resolves/rejects */
      });
    },

    signOut: async () => {
      // Mark FIRST: the POST can hang, and a user who gives up and navigates away has still signed out.
      markSignedOut();
      try {
        // POST, never GET: a GET would be triggerable by any third-party page.
        // `responseType: 'text'`: the Auth service answers `303 /logged-out`, the browser follows it, and
        // the body is an HTML page. The default JSON parse would throw "Unexpected token '<'" into the
        // global error handler on every sign-out.
        await firstValueFrom(http.post('/auth/logout', null, { observe: 'response', responseType: 'text' }));
      } finally {
        gatewayNavigation.navigate('/logged-out');
      }
    },

    fetchProfile: () => firstValueFrom(http.get<UserProfile>('/api/v1/me')),
  };
}

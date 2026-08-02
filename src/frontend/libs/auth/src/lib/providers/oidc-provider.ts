import { inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type { AuthProviderApi, AuthSession, UserProfile } from '../auth.types';
import { sanitizeReturnPath } from '../sanitize-return-path';

/**
 * Generic OIDC provider — a real Authorization Code + PKCE flow against any OIDC-compliant
 * authority (Entra ID, Google, Keycloak, Auth0, Okta, …), using only native browser APIs
 * (`fetch`, `crypto.subtle`, `URLSearchParams`). No OIDC client library is a dependency of this
 * workspace, deliberately: PKCE against a discovery document is ~200 lines and adding a library
 * would tie the boilerplate to one provider's SDK.
 *
 * The identity provider AUTHENTICATES only. Authorization (roles + capabilities) is resolved
 * server-side by `fetchProfile` (`GET /api/v1/me`) so the browser never decides what it may do.
 *
 * `signIn()` is a full-page redirect (`window.location.assign`) to the authority's
 * `authorization_endpoint`, so it deliberately never resolves on the success path — the browser
 * navigates away. The redirect back to `redirectUri` (default `/auth/callback`) must be handled
 * by a route that calls `completeOidcSignIn()` (exported below) to exchange the authorization
 * code for tokens and persist the resulting session; `apps/web` wires that up as the
 * `/auth/callback` route. `restore()` stays synchronous (the `AuthProviderApi` contract) by only
 * ever reading the already-persisted session — it does no I/O itself.
 */

export interface OidcConfig {
  /** OIDC issuer base URL; `${authority}/.well-known/openid-configuration` must resolve. */
  readonly authority: string;
  readonly clientId: string;
  /** Defaults to `${window.location.origin}/auth/callback`. */
  readonly redirectUri?: string;
  /** Defaults to `'openid profile email offline_access'` (the last scope requests a refresh token). */
  readonly scope?: string;
}

declare global {
  interface Window {
    /** Runtime auth mode (`dev` | `oidc`), written by `public/env.js`. */
    __APP_AUTH_MODE__?: string;
    /** Runtime OIDC configuration, written by `public/env.js`. Never hard-code real values. */
    __APP_OIDC_CONFIG__?: OidcConfig;
  }
}

const NOT_CONFIGURED =
  'Single sign-on is not configured in this build (window.__APP_OIDC_CONFIG__ is missing ' +
  'authority/clientId). Use the dev role picker instead.';

const SESSION_KEY = 'app.oidc-session.v1';
const ID_TOKEN_KEY = 'app.oidc-idtoken.v1';
const REFRESH_TOKEN_KEY = 'app.oidc-refresh.v1';
const PENDING_KEY = 'app.oidc-pending.v1';
const DEFAULT_SCOPE = 'openid profile email offline_access';

interface DiscoveryDocument {
  authorization_endpoint: string;
  token_endpoint: string;
  end_session_endpoint?: string;
}

interface PendingSignIn {
  verifier: string;
  state: string;
  nonce: string;
  from: string;
}

interface TokenResponse {
  access_token: string;
  id_token: string;
  refresh_token?: string;
  expires_in?: number;
  token_type: string;
}

const discoveryCache = new Map<string, Promise<DiscoveryDocument>>();

/**
 * Module-level single-flight guard for `refreshToken()`: multiple concurrent 401s (e.g. several
 * API calls in flight when the access token expires) would otherwise each independently POST the
 * OIDC `token_endpoint` with the *same* stored refresh token. Most providers treat a reused
 * refresh token as a replay and revoke the whole session. Whichever call arrives first starts the
 * request and stashes its promise here; every concurrent caller gets that same promise back
 * instead of firing its own.
 */
let refreshInFlight: Promise<string | null> | null = null;

/**
 * Full-page navigation seam: real code always delegates to `window.location.assign`. Tests
 * substitute `navigate` instead of spying on `window.location.assign` directly — jsdom's
 * `Location.assign` is a non-configurable own property, so `vi.spyOn` can't intercept it.
 */
export const oidcNavigation = {
  navigate(url: string): void {
    window.location.assign(url);
  },
};

function resolveOidcConfig(): OidcConfig | null {
  const config = window.__APP_OIDC_CONFIG__;
  if (config === undefined || config.authority.trim() === '' || config.clientId.trim() === '') {
    return null;
  }
  return config;
}

function redirectUriFor(config: OidcConfig): string {
  return config.redirectUri ?? `${window.location.origin}/auth/callback`;
}

function fetchDiscoveryDocument(authority: string): Promise<DiscoveryDocument> {
  const cached = discoveryCache.get(authority);
  if (cached !== undefined) {
    return cached;
  }
  const promise = fetch(`${authority.replace(/\/$/, '')}/.well-known/openid-configuration`)
    .then((res) => {
      if (!res.ok) {
        throw new Error(`OIDC discovery failed (${res.status}) for authority "${authority}".`);
      }
      return res.json() as Promise<DiscoveryDocument>;
    })
    .catch((err: unknown) => {
      discoveryCache.delete(authority);
      throw err;
    });
  discoveryCache.set(authority, promise);
  return promise;
}

function base64UrlEncode(bytes: Uint8Array): string {
  let binary = '';
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function base64UrlDecodeToString(value: string): string {
  const padded = value.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(value.length / 4) * 4, '=');
  return atob(padded);
}

function randomBase64Url(byteLength: number): string {
  const bytes = new Uint8Array(byteLength);
  crypto.getRandomValues(bytes);
  return base64UrlEncode(bytes);
}

async function sha256Base64Url(input: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(input));
  return base64UrlEncode(new Uint8Array(digest));
}

/** Decodes (NOT verifies) a JWT's payload — signature verification is the backend's job when the
 * token is later sent as a bearer credential; the client only needs the claims. */
function decodeJwtPayload(jwt: string): Record<string, unknown> {
  const segments = jwt.split('.');
  if (segments.length !== 3) {
    throw new Error('Malformed id_token: expected a 3-segment JWT.');
  }
  const payload: unknown = JSON.parse(base64UrlDecodeToString(segments[1] as string));
  if (typeof payload !== 'object' || payload === null) {
    throw new Error('Malformed id_token: payload is not an object.');
  }
  return payload as Record<string, unknown>;
}

function claimString(claims: Record<string, unknown>, key: string): string | undefined {
  const value = claims[key];
  return typeof value === 'string' ? value : undefined;
}

function readStoredSession(): AuthSession | null {
  try {
    const raw = window.localStorage.getItem(SESSION_KEY);
    if (raw === null) {
      return null;
    }
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) {
      return null;
    }
    const candidate = parsed as Record<string, unknown>;
    const user = candidate['user'] as Record<string, unknown> | undefined;
    if (
      typeof user !== 'object' ||
      user === null ||
      typeof user['userId'] !== 'string' ||
      typeof user['name'] !== 'string' ||
      typeof user['email'] !== 'string' ||
      !Array.isArray(candidate['roles']) ||
      typeof candidate['accessToken'] !== 'string'
    ) {
      return null;
    }
    return {
      user: { userId: user['userId'], name: user['name'], email: user['email'] },
      // Roles are deliberately NOT restored from storage — they are resolved server-side by
      // `fetchProfile`, so a tampered localStorage entry cannot grant capabilities.
      roles: [],
      accessToken: candidate['accessToken'],
    };
  } catch {
    return null;
  }
}

function persistSession(session: AuthSession, idToken: string, refreshToken: string | undefined): void {
  window.localStorage.setItem(SESSION_KEY, JSON.stringify(session));
  window.localStorage.setItem(ID_TOKEN_KEY, idToken);
  if (refreshToken !== undefined) {
    window.localStorage.setItem(REFRESH_TOKEN_KEY, refreshToken);
  }
}

function clearPersistedSession(): void {
  window.localStorage.removeItem(SESSION_KEY);
  window.localStorage.removeItem(ID_TOKEN_KEY);
  window.localStorage.removeItem(REFRESH_TOKEN_KEY);
}

async function exchangeCodeForTokens(
  config: OidcConfig,
  discovery: DiscoveryDocument,
  code: string,
  verifier: string,
): Promise<TokenResponse> {
  const body = new URLSearchParams({
    grant_type: 'authorization_code',
    code,
    redirect_uri: redirectUriFor(config),
    client_id: config.clientId,
    code_verifier: verifier,
  });
  const res = await fetch(discovery.token_endpoint, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: body.toString(),
  });
  if (!res.ok) {
    throw new Error(`Token exchange failed (${res.status}).`);
  }
  return res.json() as Promise<TokenResponse>;
}

/**
 * Completes the Authorization Code + PKCE round trip after the identity provider redirects back
 * to `redirectUri` with `?code=...&state=...`. Called by the app's `/auth/callback` route.
 * Returns the established session plus the original `from` path `signIn()` captured, so the
 * callback route knows where to send the user next.
 */
export async function completeOidcSignIn(
  locationSearch: string = window.location.search,
): Promise<{ session: AuthSession; from: string }> {
  const params = new URLSearchParams(locationSearch);
  const oidcError = params.get('error');
  if (oidcError !== null) {
    throw new Error(params.get('error_description') ?? `Sign-in failed: ${oidcError}.`);
  }
  const code = params.get('code');
  const state = params.get('state');
  if (code === null || state === null) {
    throw new Error('Sign-in callback is missing the authorization code or state.');
  }

  const pendingRaw = window.sessionStorage.getItem(PENDING_KEY);
  window.sessionStorage.removeItem(PENDING_KEY);
  if (pendingRaw === null) {
    throw new Error('No pending sign-in found for this callback (state may have been lost).');
  }
  const pending = JSON.parse(pendingRaw) as PendingSignIn;
  if (pending.state !== state) {
    throw new Error('Sign-in state mismatch — possible CSRF, aborting.');
  }

  const config = resolveOidcConfig();
  if (config === null) {
    throw new Error(NOT_CONFIGURED);
  }
  const discovery = await fetchDiscoveryDocument(config.authority);
  const tokens = await exchangeCodeForTokens(config, discovery, code, pending.verifier);
  const claims = decodeJwtPayload(tokens.id_token);
  if (claims['nonce'] !== pending.nonce) {
    throw new Error('Sign-in nonce mismatch — possible replay, aborting.');
  }

  const userId = claimString(claims, 'sub') ?? '';
  const session: AuthSession = {
    user: {
      userId,
      name: claimString(claims, 'name') ?? claimString(claims, 'email') ?? userId,
      email: claimString(claims, 'email') ?? '',
    },
    // Authentication only: roles are resolved server-side via `fetchProfile`, the same fallback
    // pattern `AuthService` already applies while that request is in flight.
    roles: [],
    accessToken: tokens.access_token,
  };
  persistSession(session, tokens.id_token, tokens.refresh_token);
  return { session, from: pending.from };
}

export function createOidcProvider(): AuthProviderApi {
  const http = inject(HttpClient);
  return {
    mode: 'oidc',
    devUsers: null,

    restore(): AuthSession | null {
      return readStoredSession();
    },

    async signIn(): Promise<AuthSession> {
      const config = resolveOidcConfig();
      if (config === null) {
        throw new Error(NOT_CONFIGURED);
      }
      const discovery = await fetchDiscoveryDocument(config.authority);
      const verifier = randomBase64Url(32);
      const challenge = await sha256Base64Url(verifier);
      const state = randomBase64Url(16);
      const nonce = randomBase64Url(16);
      const from = sanitizeReturnPath(new URLSearchParams(window.location.search).get('from') ?? '/');
      const pending: PendingSignIn = { verifier, state, nonce, from };
      window.sessionStorage.setItem(PENDING_KEY, JSON.stringify(pending));

      const authUrl = new URL(discovery.authorization_endpoint);
      authUrl.search = new URLSearchParams({
        response_type: 'code',
        client_id: config.clientId,
        redirect_uri: redirectUriFor(config),
        scope: config.scope ?? DEFAULT_SCOPE,
        state,
        nonce,
        code_challenge: challenge,
        code_challenge_method: 'S256',
      }).toString();

      oidcNavigation.navigate(authUrl.toString());
      // Navigation is in flight; this promise intentionally never settles on the success path.
      return new Promise<AuthSession>(() => {
        /* never resolves/rejects: the browser is navigating away */
      });
    },

    async refreshToken(): Promise<string | null> {
      if (refreshInFlight !== null) {
        return refreshInFlight;
      }
      const config = resolveOidcConfig();
      const refreshTokenValue = window.localStorage.getItem(REFRESH_TOKEN_KEY);
      if (config === null || refreshTokenValue === null) {
        return null;
      }
      // `attempt` is assigned to `refreshInFlight` *after* the IIFE call returns, and the clearing
      // callback is attached to `attempt` (a real Promise#finally, always microtask-deferred)
      // rather than living inside the async body's own try/finally — otherwise a synchronously
      // rejecting body could clear a guard that was never set.
      const attempt = (async () => {
        try {
          const discovery = await fetchDiscoveryDocument(config.authority);
          const body = new URLSearchParams({
            grant_type: 'refresh_token',
            refresh_token: refreshTokenValue,
            client_id: config.clientId,
          });
          const res = await fetch(discovery.token_endpoint, {
            method: 'POST',
            headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
            body: body.toString(),
          });
          if (!res.ok) {
            return null;
          }
          const tokens = (await res.json()) as TokenResponse;
          const stored = readStoredSession();
          if (stored === null) {
            return null;
          }
          const nextSession: AuthSession = { ...stored, accessToken: tokens.access_token };
          persistSession(nextSession, tokens.id_token, tokens.refresh_token ?? refreshTokenValue);
          return tokens.access_token;
        } catch {
          return null;
        }
      })();
      refreshInFlight = attempt;
      void attempt.finally(() => {
        if (refreshInFlight === attempt) {
          refreshInFlight = null;
        }
      });
      return attempt;
    },

    async signOut(): Promise<void> {
      const config = resolveOidcConfig();
      const idToken = window.localStorage.getItem(ID_TOKEN_KEY);
      clearPersistedSession();
      if (config === null || idToken === null) {
        return;
      }
      // Best-effort RP-initiated logout. `signIn()` navigated away via a full-page
      // `window.location.assign`, which wiped this module's `discoveryCache`, so by the time the
      // user signs out the cache is cold. Await the discovery document (the same single-flight
      // fetch the sign-in/refresh paths use) rather than reading the cache and bailing — bailing
      // would leave the identity provider's own SSO session alive, silently re-authenticating the
      // same account on the next sign-in from a shared machine.
      let discovery: DiscoveryDocument;
      try {
        discovery = await fetchDiscoveryDocument(config.authority);
      } catch {
        // Discovery unreachable: the local session is already cleared above, so don't hang
        // sign-out on the round trip — leave the user locally signed out.
        return;
      }
      if (discovery.end_session_endpoint === undefined) {
        return;
      }
      const logoutUrl = new URL(discovery.end_session_endpoint);
      logoutUrl.search = new URLSearchParams({
        id_token_hint: idToken,
        post_logout_redirect_uri: window.location.origin,
      }).toString();
      oidcNavigation.navigate(logoutUrl.toString());
    },

    /** The server is the authority on who the caller is and what they may do. Implement
     * `GET /api/v1/me` to return {@link UserProfile}; see `libs/auth/README.md`. */
    fetchProfile(): Promise<UserProfile> {
      return firstValueFrom(http.get<UserProfile>('/api/v1/me'));
    },
  };
}

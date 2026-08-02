import type { Capabilities, Role } from './roles';

export type AuthMode = 'dev' | 'oidc';

/** The signed-in principal. */
export interface SessionUser {
  userId: string;
  name: string;
  email: string;
}

/**
 * The authoritative identity + authorization payload for the current session. In `oidc` mode
 * this is fetched from the API (`GET /api/v1/me`) so the SERVER decides what the user may do;
 * in `dev` mode it is synthesized from the picked role so the app runs identically offline.
 *
 * Implement the matching endpoint in your backend and, once it is in the OpenAPI document,
 * replace this interface with the generated type from `@aj-boilerplate/data-access/api-types`.
 */
export interface UserProfile {
  userId: string;
  displayName: string;
  email: string;
  roles: readonly string[];
  capabilities: Capabilities;
}

/** A live authenticated session held in memory + persisted by the provider. */
export interface AuthSession {
  user: SessionUser;
  roles: Role[];
  /**
   * Bearer token for API calls. In dev mode this is a synthetic marker (there is no identity
   * provider); in oidc mode it is the access token from the authorization-code exchange.
   */
  accessToken: string;
}

/** A selectable sample user for the dev role-picker login. */
export interface DevUser {
  userId: string;
  name: string;
  email: string;
  role: Role;
}

/**
 * Strategy interface every auth provider implements. `AuthService` drives one of these based on
 * the runtime-resolved auth mode, so nothing else in the app knows which provider is active.
 */
export interface AuthProviderApi {
  readonly mode: AuthMode;
  /** Sample users for the dev picker; `null` for real identity-provider modes. */
  readonly devUsers: readonly DevUser[] | null;
  /** Rehydrate a persisted session, or `null` if none/invalid. Synchronous: never does I/O. */
  restore(): AuthSession | null;
  /** Establish a session. Dev mode requires a `userId`; oidc ignores it (and, being a full-page
   * redirect, never resolves on its success path — see `oidc-provider.ts`). */
  signIn(userId?: string): Promise<AuthSession>;
  /** Tear down the persisted session. */
  signOut(): void | Promise<void>;
  /** Resolve the authoritative {@link UserProfile} for a live session — the single capability
   * source the app gates on. */
  fetchProfile(session: AuthSession): Promise<UserProfile>;
  /**
   * Optional: refresh the access token in place, returning the new token or `null` if refreshing
   * isn't possible/failed. Providers that don't support refresh simply omit this —
   * `AuthService.refreshToken()` treats a missing method the same as a failed refresh.
   */
  refreshToken?(session: AuthSession): Promise<string | null>;
}

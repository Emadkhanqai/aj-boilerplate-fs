import type { Capabilities, Role } from './roles';

/**
 * `gateway` is the default for every deployed environment: the browser holds no token, only the
 * gateway's HttpOnly session cookie (docs/auth/gateway-contract.md). `dev` is the role picker, for
 * local UI work and e2e tests only.
 */
export type AuthMode = 'gateway' | 'dev';

/** The signed-in principal. */
export interface SessionUser {
  userId: string;
  name: string;
  email: string;
}

/**
 * The authoritative identity + authorization payload for the current session, fetched from
 * `GET /api/v1/me` so the SERVER decides what the user may do. In `dev` mode it is synthesized
 * from the picked role so the app runs identically offline.
 *
 * Hand-written on purpose, though `api-types` now generates `UserProfileResponse` from the OpenAPI
 * document: every generated property is optional and nullable (the document declares no `required`),
 * which would push a null check into every reader. `user-profile.contract.spec.ts` fails if this
 * interface names a field the generated schema does not have.
 */
export interface UserProfile {
  userId: string;
  displayName: string;
  email: string;
  roles: readonly string[];
  capabilities: Capabilities;
  /**
   * `resource:action` permissions (`items:read`, `items:write`, `admin:manage`). The API grants a
   * policy to a seat OR a permission, so the UI treats them the same way (see
   * `capabilitiesForProfile`). Optional: an API that predates permissions simply omits it.
   */
  permissions?: readonly string[];
}

/** A live authenticated session held in memory. It carries NO token: requests ride the cookie. */
export interface AuthSession {
  user: SessionUser;
  roles: Role[];
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
  /** Sample users for the dev picker; `null` in gateway mode. */
  readonly devUsers: readonly DevUser[] | null;
  /**
   * True when the credential is an HttpOnly cookie the app cannot see. A 401 off the sign-in page
   * then means THIS session ended, even though the app holds nothing that says it ever existed.
   */
  readonly ambientCredential?: boolean;
  /** Rehydrate a session, or `null` if none. Synchronous: never does I/O. */
  restore(): AuthSession | null;
  /**
   * Establish a session. Dev mode requires a `userId`. Gateway mode ignores it, takes an optional
   * `loginHint`, and being a full-page navigation never resolves.
   */
  signIn(userId?: string, loginHint?: string): Promise<AuthSession>;
  /** Tear down the session. */
  signOut(): void | Promise<void>;
  /** Resolve the authoritative {@link UserProfile} for a live session. */
  fetchProfile(session: AuthSession): Promise<UserProfile>;
}

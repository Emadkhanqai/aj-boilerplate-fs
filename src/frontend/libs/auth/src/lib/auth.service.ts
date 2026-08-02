import { Injectable, computed, inject, signal } from '@angular/core';
import { QueryClient, injectQuery } from '@tanstack/angular-query-experimental';
import { capabilitiesForRoles, isRole, NO_CAPABILITIES } from './roles';
import type { Capabilities, Role } from './roles';
import { createAuthProvider } from './providers/provider-factory';
import type { AuthMode, AuthSession, DevUser, SessionUser } from './auth.types';

/**
 * Owns the auth session and drives the configured provider (dev/oidc). Route guards, the shell,
 * and any feature inject this service directly — no context/provider component needed.
 *
 * Capability source of truth: once a session exists, `injectQuery` resolves the authoritative
 * `UserProfile` (oidc mode fetches `GET /api/v1/me`; dev mode synthesizes it) and its
 * `capabilities` flow through `capabilities()`. While that query is in flight we fall back to the
 * shared role -> capability map derived from the session's roles, so the UI never flashes
 * unauthorized-looking chrome.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly provider = createAuthProvider();
  private readonly sessionSignal = signal<AuthSession | null>(this.provider.restore());
  /**
   * The app's single shared TanStack `QueryClient` (provided via `provideTanStackQuery` in
   * `app.config.ts`). Cleared on both `signOut()` and `signIn()` — every cached query is keyed by
   * resource, not by user, so without this a sign-out/sign-in (or a dev-mode role switch, which
   * never does a full page reload) would leave the previous account's data resolvable straight
   * out of cache for whoever signs in next.
   */
  private readonly queryClient = inject(QueryClient);

  readonly mode: AuthMode = this.provider.mode;
  readonly devUsers: readonly DevUser[] | null = this.provider.devUsers;

  readonly session = this.sessionSignal.asReadonly();

  private readonly profileQuery = injectQuery(() => ({
    queryKey: ['me', this.sessionSignal()?.user.userId ?? null],
    queryFn: () => this.provider.fetchProfile(this.sessionSignal() as AuthSession),
    enabled: this.sessionSignal() !== null,
    staleTime: 5 * 60 * 1000,
  }));

  readonly isAuthenticated = computed(() => this.sessionSignal() !== null);

  readonly user = computed<SessionUser | null>(() => {
    const session = this.sessionSignal();
    if (session === null) {
      return null;
    }
    const profile = this.profileQuery.data();
    return profile !== undefined
      ? { userId: profile.userId, name: profile.displayName, email: profile.email }
      : session.user;
  });

  readonly roles = computed<Role[]>(() => {
    const session = this.sessionSignal();
    if (session === null) {
      return [];
    }
    const rawRoles = this.profileQuery.data()?.roles ?? session.roles;
    return rawRoles.filter((r): r is Role => isRole(r));
  });

  readonly capabilitiesLoading = computed(
    () => this.sessionSignal() !== null && this.profileQuery.isPending(),
  );

  readonly capabilities = computed<Capabilities>(() => {
    const session = this.sessionSignal();
    if (session === null) {
      return NO_CAPABILITIES;
    }
    return this.profileQuery.data()?.capabilities ?? capabilitiesForRoles(session.roles);
  });

  private readonly sessionExpiredSignal = signal(false);
  /** Set by `SESSION_EXPIRED_NOTIFIER` (bound in app.config.ts) when `authInterceptor` sees a 401
   * it couldn't resolve via `refreshToken()` — drives the session-expired UX (the shell redirects
   * to `/login`, which reads this to show a "please sign in again" message). Cleared on the next
   * successful `signIn()`. */
  readonly sessionExpired = this.sessionExpiredSignal.asReadonly();

  async signIn(userId?: string): Promise<void> {
    const next = await this.provider.signIn(userId);
    // Clear before adopting the new session: a real-provider sign-in reloads the page anyway
    // (see auth-callback-page.ts), but dev mode's role picker never does, so any query cached
    // under the previous account must not still be resolvable for the new one.
    this.queryClient.clear();
    this.sessionSignal.set(next);
    this.sessionExpiredSignal.set(false);
  }

  signOut(): void {
    void this.provider.signOut();
    this.sessionSignal.set(null);
    this.queryClient.clear();
  }

  /**
   * Attempts to refresh the current session's access token in place (delegates to the provider's
   * optional `refreshToken`; dev mode has none, so this resolves `null` immediately). Bound to
   * `TOKEN_REFRESHER` in app.config.ts, driving `authInterceptor`'s refresh-on-401 retry.
   */
  async refreshToken(): Promise<string | null> {
    const session = this.sessionSignal();
    if (session === null || this.provider.refreshToken === undefined) {
      return null;
    }
    const newToken = await this.provider.refreshToken(session);
    if (newToken === null) {
      return null;
    }
    this.sessionSignal.set({ ...session, accessToken: newToken });
    return newToken;
  }

  /** Bound to `SESSION_EXPIRED_NOTIFIER` in app.config.ts. */
  notifySessionExpired(): void {
    this.sessionSignal.set(null);
    this.sessionExpiredSignal.set(true);
  }

  /** Read the current bearer token synchronously — bound to `AUTH_TOKEN_PROVIDER` in app.config.ts. */
  readonly currentToken = (): string | null => this.sessionSignal()?.accessToken ?? null;
}

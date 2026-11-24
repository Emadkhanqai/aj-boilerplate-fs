import { Injectable, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationCancel, NavigationEnd, NavigationError, NavigationStart, Router } from '@angular/router';
import { QueryClient, injectQuery } from '@tanstack/angular-query-experimental';
import { filter, map } from 'rxjs';
import { capabilitiesForProfile, capabilitiesForRoles, NO_CAPABILITIES, toRoles } from './roles';
import type { Capabilities, Role } from './roles';
import type { RoleRecheck } from './role-change';
import { isAuthoritativeCapabilityAnswer } from './capability-availability';
import { createAuthProvider } from './providers/provider-factory';
import type { AuthMode, AuthSession, DevUser, SessionUser } from './auth.types';

/** The sign-in surface: the one route the gateway serves without a session. */
function isSignInPath(pathname: string): boolean {
  return pathname === '/login' || pathname.startsWith('/login/');
}

/**
 * Owns the auth session and drives the configured provider (gateway/dev). Route guards, the shell
 * and any feature inject this service directly.
 *
 * Capability source of truth: once a session exists, `injectQuery` resolves the authoritative
 * `UserProfile` (`GET /api/v1/me`; dev mode synthesizes it) and its capabilities flow through
 * `capabilities()`. While that query is in flight we fall back to the role -> capability map for the
 * session's roles, so the UI never flashes unauthorized-looking chrome.
 *
 * In gateway mode the session is an AMBIENT one: the app cannot see the cookie, so `session` is a
 * grantless placeholder and the profile query is what proves a real session exists.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly provider = createAuthProvider();
  private readonly sessionSignal = signal<AuthSession | null>(this.provider.restore());
  /**
   * The app's single shared TanStack `QueryClient`. Cleared on sign-out and sign-in: every cached
   * query is keyed by resource, not by user, so without this a dev-mode role switch (which never
   * reloads the page) would leave the previous account's data resolvable from cache.
   */
  private readonly queryClient = inject(QueryClient);
  private readonly router = inject(Router);

  readonly mode: AuthMode = this.provider.mode;
  readonly devUsers: readonly DevUser[] | null = this.provider.devUsers;

  readonly session = this.sessionSignal.asReadonly();

  /**
   * Whether the router is on (or navigating to) `/login`. The gateway serves that page WITHOUT a
   * session, so `/api/v1/me` would 401 there; the profile query waits until the user is off it.
   */
  readonly onSignInPage = toSignal(
    this.router.events.pipe(
      filter(
        (e) =>
          e instanceof NavigationStart ||
          e instanceof NavigationEnd ||
          e instanceof NavigationCancel ||
          e instanceof NavigationError,
      ),
      map((e) => isSignInPath((e instanceof NavigationStart ? e.url : this.router.url).split(/[?#]/)[0] ?? '')),
    ),
    { initialValue: isSignInPath(globalThis.location?.pathname ?? '') },
  );

  private readonly profileQuery = injectQuery(() => ({
    queryKey: ['me', this.sessionSignal()?.user.userId ?? null],
    queryFn: () => this.provider.fetchProfile(this.sessionSignal() as AuthSession),
    enabled: this.sessionSignal() !== null && !this.onSignInPage(),
    staleTime: 5 * 60 * 1000,
  }));

  readonly isAuthenticated = computed(() => this.sessionSignal() !== null);

  /** True once `/api/v1/me` has answered: the only proof, in gateway mode, that a session exists. */
  readonly sessionConfirmed = computed(() => this.sessionSignal() !== null && this.profileQuery.data() !== undefined);

  readonly user = computed<SessionUser | null>(() => {
    const session = this.sessionSignal();
    if (session === null) {
      return null;
    }
    const profile = this.profileQuery.data();
    return profile === undefined
      ? session.user
      : { userId: profile.userId, name: profile.displayName, email: profile.email };
  });

  readonly roles = computed<Role[]>(() => {
    const session = this.sessionSignal();
    if (session === null) {
      return [];
    }
    // `toRoles`, not an `isRole` filter: the API returns canonical names (`"Admin"`).
    return toRoles(this.profileQuery.data()?.roles ?? session.roles);
  });

  readonly capabilitiesLoading = computed(() => this.sessionSignal() !== null && this.profileQuery.isPending());

  /**
   * The profile could not be read for a reason that is NOT the server's verdict (5xx, a 503 from the
   * gateway, network). Capabilities are then UNKNOWN rather than empty, and routes send the user to
   * "access check unavailable" instead of telling them they have no access.
   */
  readonly capabilitiesUnavailable = computed(() => {
    if (this.sessionSignal() === null || this.profileQuery.data() !== undefined) {
      return false;
    }
    return this.profileQuery.isError() && !isAuthoritativeCapabilityAnswer(this.profileQuery.error());
  });

  readonly capabilitiesError = computed<unknown>(() => (this.capabilitiesUnavailable() ? this.profileQuery.error() : null));

  readonly capabilities = computed<Capabilities>(() => {
    const session = this.sessionSignal();
    if (session === null) {
      return NO_CAPABILITIES;
    }
    const profile = this.profileQuery.data();
    return profile === undefined ? capabilitiesForRoles(session.roles) : capabilitiesForProfile(profile);
  });

  /** Re-reads `/api/v1/me`, coalescing concurrent callers into one request. */
  refreshProfile(): Promise<void> {
    this.refreshing ??= this.refetchProfile().finally(() => {
      this.refreshing = null;
    });
    return this.refreshing;
  }

  private refreshing: Promise<void> | null = null;

  private async refetchProfile(): Promise<void> {
    if (this.sessionSignal() === null || this.onSignInPage()) {
      return;
    }
    await this.profileQuery.refetch();
  }

  /** Re-runs the profile fetch for the "access check unavailable" page; resolves true once answered. */
  async refreshCapabilities(): Promise<boolean> {
    if (this.sessionSignal() === null) {
      return false;
    }
    const result = await this.profileQuery.refetch();
    // Let the reactive state settle before the caller navigates: the guard reads signals.
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
    return result.data !== undefined;
  }

  /**
   * Forces a `/api/v1/me` refetch and reports the capability set before and after, for the
   * role-change watcher. `null` when no honest comparison is possible (no session, on the sign-in
   * page, or the refetch failed).
   */
  async recheckCapabilities(): Promise<RoleRecheck | null> {
    if (this.sessionSignal() === null || this.onSignInPage()) {
      return null;
    }
    const prior = this.profileQuery.data();
    const before = prior === undefined ? null : capabilitiesForProfile(prior);
    const result = await this.profileQuery.refetch();
    if (result.status !== 'success' || result.data === undefined) {
      return null;
    }
    return { before, after: capabilitiesForProfile(result.data) };
  }

  private readonly sessionExpiredSignal = signal(false);
  /** Set when a request on a live session came back 401. The shell redirects to `/login`, which shows a notice. */
  readonly sessionExpired = this.sessionExpiredSignal.asReadonly();

  async signIn(userId?: string, loginHint?: string): Promise<void> {
    const next = await this.provider.signIn(userId, loginHint);
    // Clear before adopting the new session: dev mode's role picker never reloads the page.
    this.queryClient.clear();
    this.sessionSignal.set(next);
    this.sessionExpiredSignal.set(false);
  }

  signOut(): void {
    void this.provider.signOut();
    this.sessionSignal.set(null);
    this.queryClient.clear();
  }

  /** Bound to `SESSION_EXPIRED_NOTIFIER`. A no-op in dev mode, where there is no server session to lose. */
  notifySessionExpired(): void {
    if (this.mode === 'dev' || this.sessionSignal() === null) {
      return;
    }
    this.sessionSignal.set(null);
    this.sessionExpiredSignal.set(true);
  }

  /**
   * Bound to `AMBIENT_SESSION_ACTIVE`. True when the credential is a cookie the app cannot see, a
   * session is held, and we are NOT on the sign-in page: a 401 then means the session ended. A 401
   * on the sign-in page answers an anonymous visitor and is not "expired".
   */
  hasAmbientSession(): boolean {
    if (this.provider.ambientCredential !== true || this.sessionSignal() === null) {
      return false;
    }
    return !isSignInPath(globalThis.location?.pathname ?? '');
  }
}

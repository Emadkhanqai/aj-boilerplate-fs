import { inject } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import type { CanActivateFn, UrlTree } from '@angular/router';
import { filter, map, take } from 'rxjs';
import type { Observable } from 'rxjs';
import { AuthService } from './auth.service';
import type { Capability } from './roles';

/**
 * Capability guard for role-gated routes. If the session lacks the capability, redirects to a
 * visible "access denied" page rather than silently bouncing to the home page — a missing
 * capability is worth surfacing, not hiding. Mirrors the backend policy on the underlying
 * endpoints; the backend remains the real enforcement point.
 *
 * Capabilities are only authoritative once the profile query has resolved: on a cold page load
 * (bookmark, refresh, pasted link) the session carries no usable roles yet, so
 * `AuthService.capabilities()` is still serving its all-false fallback. Deciding then would deny
 * every capability-gated route to every role, so the guard waits for `capabilitiesLoading()` to
 * clear before it judges. `capabilitiesLoading()` is already false when there is no session at
 * all, so the guard resolves (to a deny) instead of hanging — `authGuard` on the parent route is
 * what redirects an unauthenticated visitor to /login.
 */
export function capabilityGuard(capability: Capability): CanActivateFn {
  return (): boolean | UrlTree | Observable<boolean | UrlTree> => {
    const authService = inject(AuthService);
    const router = inject(Router);

    const decide = (): boolean | UrlTree =>
      authService.capabilities()[capability] ||
      router.createUrlTree(['/access-denied'], { queryParams: { capability } });

    if (!authService.capabilitiesLoading()) {
      return decide();
    }
    return toObservable(authService.capabilitiesLoading).pipe(
      filter((loading) => !loading),
      take(1),
      map(() => decide()),
    );
  };
}

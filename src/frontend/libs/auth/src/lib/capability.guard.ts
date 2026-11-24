import { inject } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import type { CanActivateFn, UrlTree } from '@angular/router';
import { filter, map, take } from 'rxjs';
import type { Observable } from 'rxjs';
import { AuthService } from './auth.service';
import { sanitizeReturnPath } from './sanitize-return-path';
import type { Capability } from './roles';

export const ACCESS_CHECK_UNAVAILABLE_PATH = '/access-check-unavailable';

/**
 * Capability guard for role-gated routes. Three outcomes, kept distinct on purpose:
 *
 * - no session -> `/login`;
 * - the profile could NOT be read (outage, not a verdict) -> `/access-check-unavailable`, because
 *   telling someone they have no access when the server simply did not answer is a false statement;
 * - the profile says no -> `/access-denied`, a visible refusal rather than a silent bounce.
 *
 * Capabilities are only authoritative once the profile query has resolved: on a cold page load the
 * session carries no usable roles yet, so deciding then would deny every gated route to every role.
 * The guard waits for `capabilitiesLoading()` to clear. Mirrors the backend policy on the underlying
 * endpoints; the backend remains the real enforcement point.
 */
export function capabilityGuard(capability: Capability): CanActivateFn {
  return (_route, state): boolean | UrlTree | Observable<boolean | UrlTree> => {
    const authService = inject(AuthService);
    const router = inject(Router);

    const decide = (): boolean | UrlTree => {
      const from = sanitizeReturnPath(state.url);
      if (!authService.isAuthenticated()) {
        return from === '/' ? router.createUrlTree(['/login']) : router.createUrlTree(['/login'], { queryParams: { from } });
      }
      if (authService.capabilitiesUnavailable()) {
        return router.createUrlTree([ACCESS_CHECK_UNAVAILABLE_PATH], from === '/' ? {} : { queryParams: { from } });
      }
      return authService.capabilities()[capability] || router.createUrlTree(['/access-denied'], { queryParams: { capability } });
    };

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

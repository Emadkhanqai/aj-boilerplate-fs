import { inject } from '@angular/core';
import { Router } from '@angular/router';
import type { CanActivateFn } from '@angular/router';
import { AuthService } from './auth.service';
import { sanitizeReturnPath } from './sanitize-return-path';

/**
 * Guards the whole authenticated route group. Redirects to `/login` (preserving the attempted URL
 * as `from`, so sign-in can return the user there) when no session exists.
 *
 * In gateway mode the session is ambient and always "present" here: the real gate is nginx, which
 * never serves the shell without a valid session. This guard is the dev-mode gate and the
 * after-sign-out/expiry gate. UX routing only — the backend authorizes every request.
 *
 * `state.url` is sanitized when captured, not only where it is consumed: identity providers echo
 * protocol parameters (`/?state=…`) onto the post-logout landing URL, and capturing that verbatim
 * puts the blob in the address bar and compounds it across sign-out/sign-in cycles.
 */
export const authGuard: CanActivateFn = (_route, state) => {
  const authService = inject(AuthService);
  const router = inject(Router);
  if (authService.isAuthenticated()) {
    return true;
  }
  const from = sanitizeReturnPath(state.url);
  return from === '/' ? router.createUrlTree(['/login']) : router.createUrlTree(['/login'], { queryParams: { from } });
};

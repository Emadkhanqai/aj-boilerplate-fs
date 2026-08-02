import { inject } from '@angular/core';
import { Router } from '@angular/router';
import type { CanActivateFn } from '@angular/router';
import { AuthService } from './auth.service';
import { sanitizeReturnPath } from './sanitize-return-path';

/**
 * Guards the whole authenticated route group. Redirects to `/login` (preserving the attempted URL
 * as a query param so login can return the user there) when no session exists.
 *
 * NB: UX routing only — every API call is independently authorized by the backend.
 *
 * `state.url` is sanitized before it is stored: identity providers routinely echo their own
 * protocol parameters back onto the post-logout landing URL (`/?state=eyJpZCI6…`), and capturing
 * that verbatim puts the blob in the visible address bar and lets it compound across
 * sign-out/sign-in cycles. Sanitizing at CAPTURE — not only at the sinks that consume `from` —
 * keeps the URL the user actually sees clean too. See `sanitize-return-path.ts`.
 */
export const authGuard: CanActivateFn = (_route, state) => {
  const authService = inject(AuthService);
  const router = inject(Router);
  if (authService.isAuthenticated()) {
    return true;
  }
  return router.createUrlTree(['/login'], { queryParams: { from: sanitizeReturnPath(state.url) } });
};

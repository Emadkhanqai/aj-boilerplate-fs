import { InjectionToken, inject } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import type { HttpHeaders, HttpInterceptorFn } from '@angular/common/http';
import { tap } from 'rxjs';

/** Response header the gateway adds to a definitive "this user has no role here" 403. */
export const ACCESS_REVOKED_HEADER = 'X-Usp-Access';
export const ACCESS_REVOKED_VALUE = 'revoked';

/**
 * Called once the first time a revoked verdict is seen. Bound in `app.config.ts` to the shell's
 * `AccessRevokedWatcher`; a token, because `data-access` may not import the shell.
 */
export const ACCESS_REVOKED_OBSERVER = new InjectionToken<() => void>('ACCESS_REVOKED_OBSERVER');

/**
 * Called on any OTHER 403 (not the profile read itself). A 403 is the cheapest signal that the
 * caller's roles changed, so the role-change watcher re-checks immediately instead of waiting for
 * its next poll.
 */
export const FORBIDDEN_OBSERVER = new InjectionToken<() => void>('FORBIDDEN_OBSERVER');

const PROFILE_PATH = '/api/v1/me';

/** `403` AND the revoked header. A bare 403 is an ordinary "not allowed" and must not open the dialog. */
export function isAccessRevokedResponse(status: number, headers: HttpHeaders | undefined): boolean {
  return status === 403 && headers?.get(ACCESS_REVOKED_HEADER)?.trim() === ACCESS_REVOKED_VALUE;
}

/**
 * Latched once a revoked verdict has been seen, and never cleared until the page reloads: the
 * dialog's only way out is signing out. While it is set, `authInterceptor` stops turning 401s into
 * a redirect to `/login` — otherwise the redirect would tear the dialog down and bounce the user
 * through a sign-in that can only fail again.
 */
let accessRevokedPage = false;

export function isAccessRevokedPage(): boolean {
  return accessRevokedPage;
}

/** Test seam: the latch is module state and would leak between specs. */
export function resetAccessRevokedPageForTests(): void {
  accessRevokedPage = false;
}

/**
 * Observes failed responses. Must sit INSIDE `envelopeInterceptor` (last in the list) so it sees
 * the raw `HttpErrorResponse` with its headers, before the envelope turns it into an `ApiError`.
 */
export const accessRevokedInterceptor: HttpInterceptorFn = (req, next) => {
  const observe = inject(ACCESS_REVOKED_OBSERVER, { optional: true });
  const observeForbidden = inject(FORBIDDEN_OBSERVER, { optional: true });
  return next(req).pipe(
    tap({
      error: (err: unknown) => {
        if (!(err instanceof HttpErrorResponse)) {
          return;
        }
        if (!isAccessRevokedResponse(err.status, err.headers)) {
          if (err.status === 403 && req.url.split('?')[0] !== PROFILE_PATH) {
            observeForbidden?.();
          }
          return;
        }
        accessRevokedPage = true;
        observe?.();
      },
    }),
  );
};

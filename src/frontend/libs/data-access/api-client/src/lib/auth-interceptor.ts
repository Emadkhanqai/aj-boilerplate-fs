import { inject } from '@angular/core';
import type { HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
import { AMBIENT_SESSION_ACTIVE, SESSION_EXPIRED_NOTIFIER, markSessionExpired } from './session-expiry';
import { isAccessRevokedPage } from './access-revoked';
import { ApiError } from './api-error';

/**
 * Turns a `401` on a live session into "your session ended". It attaches NOTHING to the request:
 * the browser holds no token, same-origin requests carry the gateway's HttpOnly cookie, and nginx
 * adds the `Authorization` header on its way to the API (overwriting anything sent from here, so a
 * header attached in the browser would only be discarded and confuse whoever debugs it).
 *
 * Rules, in order:
 *  - a 401/403 while the access-revoked dialog is open is swallowed (marked, no redirect): the
 *    dialog is already the answer, and a redirect would tear it down;
 *  - a 401 while an ambient session is active and we are off the sign-in page means the session
 *    ended: notify, so the shell redirects to `/login`;
 *  - any other 401 (an anonymous visitor on the sign-in page) is just an error.
 *
 * Only a 401 may become a login redirect. A 403 or 503 never does: those are verdicts or outages,
 * not an ended session. Must be listed BEFORE `envelopeInterceptor` so it sees the unwrapped `ApiError`.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const ambientSessionActive = inject(AMBIENT_SESSION_ACTIVE, { optional: true });
  const notifySessionExpired = inject(SESSION_EXPIRED_NOTIFIER, { optional: true });

  return next(req).pipe(
    catchError((err: unknown) => {
      const status = err instanceof ApiError ? err.status : undefined;
      if ((status === 401 || status === 403) && isAccessRevokedPage()) {
        markSessionExpired(err);
      } else if (status === 401 && ambientSessionActive?.() === true) {
        markSessionExpired(err);
        notifySessionExpired?.();
      }
      return throwError(() => err);
    }),
  );
};

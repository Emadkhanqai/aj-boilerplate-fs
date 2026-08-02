import { inject } from '@angular/core';
import type { HttpInterceptorFn } from '@angular/common/http';
import { catchError, from, switchMap, throwError } from 'rxjs';
import { AUTH_TOKEN_PROVIDER } from './auth-token';
import { SESSION_EXPIRED_NOTIFIER, TOKEN_REFRESHER, markSessionExpired } from './session-expiry';
import { ApiError } from './api-error';

/**
 * Attaches `Authorization: Bearer <token>` to every API request.
 *
 * Refresh-on-401: a `401` on a request that CARRIED a bearer token means the session is no longer
 * valid server-side (expired or revoked) — not that the endpoint is simply unauthenticated. When
 * that happens, this attempts one token refresh via `TOKEN_REFRESHER` (bound by `AuthService`;
 * optional, so dev-mode sessions and any session without a refresh token skip straight to the
 * fallback) and retries the request exactly once with the new token. If there is no refresher,
 * the refresh fails/returns `null`, or the retried request still 401s, `SESSION_EXPIRED_NOTIFIER`
 * fires (also bound by `AuthService`, to clear the session and drive the session-expired UX) and
 * the original error propagates to the caller.
 *
 * If your API has anonymous surfaces that must NEVER receive the bearer token, skip them by URL
 * prefix at the top of this function rather than relying on each caller to opt out.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const getToken = inject(AUTH_TOKEN_PROVIDER);
  const refreshToken = inject(TOKEN_REFRESHER, { optional: true });
  const notifySessionExpired = inject(SESSION_EXPIRED_NOTIFIER, { optional: true });

  const token = getToken();
  const authedReq = token === null ? req : req.clone({ setHeaders: { Authorization: `Bearer ${token}` } });

  return next(authedReq).pipe(
    catchError((err: unknown) => {
      const status = err instanceof ApiError ? err.status : undefined;
      // A 401 only signals an invalid session when the request actually carried a bearer token —
      // an anonymous request 401ing (no token to begin with) is a different failure.
      if (status !== 401 || token === null) {
        return throwError(() => err);
      }
      if (refreshToken === null) {
        markSessionExpired(err);
        notifySessionExpired?.();
        return throwError(() => err);
      }
      return from(refreshToken()).pipe(
        switchMap((newToken) => {
          if (newToken === null) {
            markSessionExpired(err);
            notifySessionExpired?.();
            return throwError(() => err);
          }
          const retriedReq = req.clone({ setHeaders: { Authorization: `Bearer ${newToken}` } });
          return next(retriedReq).pipe(
            catchError((retryErr: unknown) => {
              markSessionExpired(retryErr);
              notifySessionExpired?.();
              return throwError(() => retryErr);
            }),
          );
        }),
      );
    }),
  );
};

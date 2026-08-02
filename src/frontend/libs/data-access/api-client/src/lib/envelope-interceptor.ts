import type { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { HttpResponse } from '@angular/common/http';
import { catchError, map, throwError } from 'rxjs';
import { ApiError } from './api-error';
import type { ApiResponse } from '@aj-boilerplate/data-access/api-types';

/**
 * Unwraps every JSON response's `ApiResponse<T>` envelope so downstream services see the plain
 * DTO. This is why no feature service in the app ever writes `response.data` — do the unwrap in
 * ONE place or every caller re-implements it slightly differently.
 *
 * Throws `ApiError` when `success: false`, whether that arrives on a 2xx or a non-2xx HTTP status
 * (the backend can report a logical failure with `200 OK`). Blob response bodies (file downloads,
 * `responseType: 'blob'`) are never JSON envelopes — passed through unchanged rather than
 * corrupted into `null` by the unwrap logic below.
 */
export const envelopeInterceptor: HttpInterceptorFn = (req, next) =>
  next(req).pipe(
    map((event) => {
      if (!(event instanceof HttpResponse)) {
        return event;
      }
      if (event.body instanceof Blob) {
        return event;
      }
      const envelope = event.body as Partial<ApiResponse<unknown>> | null;
      if (envelope?.success === false) {
        throw new ApiError(
          event.status,
          envelope.message ?? `Request to ${req.url} failed (${event.status}).`,
          envelope,
          envelope.code,
          envelope.traceId,
        );
      }
      return event.clone({ body: envelope?.data ?? null });
    }),
    catchError((err: unknown) => {
      if (err instanceof ApiError) {
        return throwError(() => err);
      }
      const httpErr = err as HttpErrorResponse;
      const envelope = httpErr.error as Partial<ApiResponse<unknown>> | undefined;
      return throwError(
        () =>
          new ApiError(
            httpErr.status,
            envelope?.message ?? `Request to ${req.url} failed (${httpErr.status}).`,
            envelope ?? httpErr.error,
            envelope?.code,
            envelope?.traceId,
          ),
      );
    }),
  );

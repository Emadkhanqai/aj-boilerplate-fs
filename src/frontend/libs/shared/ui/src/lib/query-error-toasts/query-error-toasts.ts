import { inject, InjectionToken } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { MutationCache, QueryCache, QueryClient } from '@tanstack/angular-query-experimental';
import { MessageService } from 'primeng/api';
import { apiErrorMessage, isSessionExpiredError } from '@aj-boilerplate/data-access/api-client';

/**
 * App-wide query retry policy. A 4xx is a CLIENT error: retrying can never fix it, so fail into
 * the error state immediately rather than sitting on a spinner through three backed-off retries.
 * Everything else (5xx, a dropped connection with no HTTP status) gets up to 2 retries —
 * transient server/network blips still self-heal.
 */
export function retryPolicy(failureCount: number, error: unknown): boolean {
  if (error instanceof HttpErrorResponse && error.status >= 400 && error.status < 500) {
    return false;
  }
  return failureCount < 2;
}

/**
 * Set on a query's or mutation's `meta` (`injectQuery(() => ({ ..., meta: GLOBAL_ERROR_TOAST_SUPPRESSED }))` /
 * `injectMutation(() => ({ ..., meta: GLOBAL_ERROR_TOAST_SUPPRESSED }))`) for any query or
 * mutation that already renders its own local error feedback — an inline `role="alert"` message,
 * a form-level error signal, an `isError()`-gated "could not load" block, a conflict banner,
 * etc. Stops `reportQueryError` from also popping a redundant generic toast on top of that local
 * UX.
 *
 * Leave it off (the default) for a query or mutation with no local error handling at all — that
 * one gets the global toast as a safety net rather than failing silently.
 */
export const GLOBAL_ERROR_TOAST_SUPPRESSED: { readonly suppressGlobalToast: true } = { suppressGlobalToast: true };

function isSuppressed(meta: unknown): boolean {
  return typeof meta === 'object' && meta !== null && (meta as Record<string, unknown>)['suppressGlobalToast'] === true;
}

/**
 * Turns a query/mutation failure into a generic error toast — the app-wide safety net for API
 * failures that would otherwise fail silently (a list query 401ing or 502ing with nothing at all
 * on screen to explain the emptiness). Wired as both `QueryCache.onError` and
 * `MutationCache.onError` in {@link QUERY_CLIENT} so every query and mutation gets covered by
 * default, without every feature component needing its own error handling.
 *
 * Skipped for:
 *  - a 401 already surfaced via `SESSION_EXPIRED_NOTIFIER` (`isSessionExpiredError`) — the
 *    login page's own "session expired" message already covers it; a toast on top would be
 *    redundant noise right as the redirect happens.
 *  - a query or mutation tagged {@link GLOBAL_ERROR_TOAST_SUPPRESSED} in its `meta` — it already
 *    renders its own local error (or, for a mutation, success/error) feedback, so a global toast
 *    would double up.
 *
 * Never fires a "success" toast — only errors — so read-only fetches and mutations with their
 * own local success handling stay exactly as noisy (or quiet) as they are today.
 */
export function reportQueryError(messageService: MessageService, error: unknown, meta?: unknown): void {
  if (isSessionExpiredError(error) || isSuppressed(meta)) {
    return;
  }
  messageService.add({
    severity: 'error',
    summary: 'Something went wrong',
    detail: apiErrorMessage(error, 'Something went wrong. Please try again.'),
    life: 6000,
  });
}

/**
 * Injection token for the app's single `QueryClient`. Built via a DI factory (rather than
 * `provideTanStackQuery(new QueryClient())` in `app.config.ts`) because its `queryCache` and
 * `mutationCache` `onError` hooks need `MessageService` injected — see
 * `@tanstack/angular-query-experimental`'s `provideTanStackQuery` docs, "using an
 * InjectionToken" — to surface `reportQueryError` as a toast for every otherwise-unhandled API
 * failure app-wide.
 */
export const QUERY_CLIENT = new InjectionToken<QueryClient>('QUERY_CLIENT', {
  factory: () => {
    const messageService = inject(MessageService);
    return new QueryClient({
      defaultOptions: {
        queries: { retry: retryPolicy },
      },
      queryCache: new QueryCache({
        onError: (error, query) => reportQueryError(messageService, error, query.meta),
      }),
      mutationCache: new MutationCache({
        onError: (error, _variables, _onMutateResult, mutation) => reportQueryError(messageService, error, mutation.meta),
      }),
    });
  },
});

import { ErrorHandler, Injectable, InjectionToken, inject, makeEnvironmentProviders, type EnvironmentProviders } from '@angular/core';
import { MessageService } from 'primeng/api';
import { ApiError, apiErrorMessage, isSessionExpiredError } from '@aj-boilerplate/data-access/api-client';

/**
 * Everything known about one unhandled error, flattened into a plain object so it can be logged,
 * shipped to a monitoring service, and asserted in a test without any of the three needing to
 * re-derive it. Deliberately structured rather than a pre-formatted string: a string is what you
 * write when you have already decided nobody will ever query these.
 */
export interface ErrorReport {
  /** Stable identity for "the same error happening again" — see {@link errorSignature}. */
  readonly signature: string;
  /** 1 for the first sighting of this signature this session, 2 for the next, and so on. */
  readonly occurrence: number;
  readonly name: string;
  readonly message: string;
  readonly stack: string | null;
  /** The page the user was on. An error report without this is a bug report without a location. */
  readonly url: string | null;
  readonly timestamp: string;
  /** HTTP status, when the error came from the API layer. */
  readonly status: number | null;
  /** Server-assigned error code, when the API sent one. */
  readonly code: string | null;
  /** Correlation id from the `ApiResponse` envelope — the one field that lets support join this
   * report to a specific server-side request. */
  readonly traceId: string | null;
}

/**
 * ============================================================================
 * THE MONITORING SEAM — this is the thing a consuming project plugs into.
 * ============================================================================
 *
 * Deliberately shaped like Sentry's `captureException(error, context)` so wiring one up is a
 * five-line adapter, while this workspace keeps ZERO third-party monitoring dependencies. A
 * boilerplate must not choose your observability vendor, and it must not ship a dependency
 * ninety percent of consumers will delete.
 *
 * To wire Sentry (or Datadog, Rollbar, App Insights, an internal `/api/v1/client-errors`
 * endpoint — the shape is the same), add ONE provider in `apps/web/src/app/app.config.ts`:
 *
 * ```ts
 * {
 *   provide: ERROR_MONITOR,
 *   useValue: {
 *     captureException: (error: unknown, report: ErrorReport) =>
 *       Sentry.captureException(error, { extra: { ...report } }),
 *   } satisfies ErrorMonitor,
 * }
 * ```
 *
 * Nothing else changes. Until that provider exists the handler still logs and still toasts —
 * it degrades to console-only, it never degrades to silence.
 */
export interface ErrorMonitor {
  captureException(error: unknown, report: ErrorReport): void;
}

/** DI token for the {@link ErrorMonitor}. Optional: unprovided in this boilerplate by design. */
export const ERROR_MONITOR = new InjectionToken<ErrorMonitor>('ERROR_MONITOR');

/**
 * How long an identical error stays "already toasted". Long enough that a poll failing every
 * second cannot stack fifty toasts; short enough that a user who hits the same failure again
 * minutes later still gets told, rather than clicking a dead button in silence.
 */
export const DUPLICATE_ERROR_WINDOW_MS = 10_000;

/**
 * Cap on distinct signatures tracked at once. Without it, an app running for a day with errors
 * carrying unique messages (an id interpolated into the text, say) grows this map forever — a
 * memory leak inside the very component meant to make failures visible.
 */
const MAX_TRACKED_SIGNATURES = 100;

/**
 * Identity for "the same error, again". Name + message + the first stack frame: the message alone
 * merges two genuinely different bugs that happen to share generic copy ("Network error"), and the
 * whole stack splits one bug into many when frames differ by an inlined async hop.
 *
 * Exported for direct unit testing — the dedupe is only as good as this function.
 */
export function errorSignature(error: unknown): string {
  if (!(error instanceof Error)) {
    return `non-error:${String(error)}`;
  }
  const firstFrame = error.stack?.split('\n').find((line) => line.trim().startsWith('at '))?.trim() ?? '';
  return `${error.name}:${error.message}:${firstFrame}`;
}

/** Flattens an unknown thrown value into the structured {@link ErrorReport} shape. */
export function describeError(error: unknown, occurrence: number, url: string | null): ErrorReport {
  const isError = error instanceof Error;
  const isApiError = error instanceof ApiError;
  return {
    signature: errorSignature(error),
    occurrence,
    name: isError ? error.name : typeof error,
    message: isError ? error.message : String(error),
    stack: isError ? (error.stack ?? null) : null,
    url,
    timestamp: new Date().toISOString(),
    status: isApiError ? error.status : null,
    code: isApiError ? (error.code ?? null) : null,
    traceId: isApiError ? (error.traceId ?? null) : null,
  };
}

/**
 * The application's global `ErrorHandler`, replacing Angular's default (which writes to the
 * console and stops there).
 *
 * It exists because of a specific failure mode: an exception thrown outside a query or a mutation
 * — in an `effect`, a subscribe callback, an event handler, a router resolver — reaches Angular's
 * default handler, prints to a console nobody has open, and the user is left looking at a button
 * that does nothing. Nothing on screen, nothing in a dashboard, no one paged. That is how a
 * production fault stays invisible for a week.
 *
 * Three guarantees, in this order, and the order is the design:
 *
 * 1. **It always logs.** Unconditionally, first statement, before any filtering can apply. Every
 *    other step in this method can be skipped by some rule; this one cannot.
 * 2. **It always reports to the monitor**, when one is wired ({@link ERROR_MONITOR}), including
 *    the repeat count — so the service whose entire job is seeing the storm actually sees its
 *    size. Sampling belongs in the monitoring SDK, which knows about rate limits; dropping events
 *    on the way in would hide the rate from the only system that could measure it.
 * 3. **It toasts, deduplicated.** Only this last step is suppressed for repeats, because it is
 *    the only one with a human on the other end. Fifty identical toasts convey no more than one
 *    and cover the UI while doing it.
 *
 * Complements — does not replace — the query/mutation error toasts in `query-error-toasts.ts`.
 * That one covers API calls TanStack Query knows about; this one covers everything else. Errors
 * already handled there never arrive here.
 *
 * Known and accepted: a stale-chunk load error (see `apps/web/src/chunk-load-error-handler.ts`)
 * can toast here a moment before that handler reloads the page. A toast that flashes during a
 * recovering reload is a better failure than suppressing a class of error at this layer.
 */
@Injectable()
export class GlobalErrorHandler implements ErrorHandler {
  private readonly messages = inject(MessageService);
  private readonly monitor = inject(ERROR_MONITOR, { optional: true });

  /** signature -> how many times seen, and when it last produced a toast. */
  private readonly seen = new Map<string, { count: number; lastToastAt: number | null }>();

  /** Guards against an error thrown *by this handler* re-entering it and recursing forever. */
  private reporting = false;

  handleError(error: unknown): void {
    const signature = errorSignature(error);
    const entry = this.seen.get(signature) ?? { count: 0, lastToastAt: null };
    entry.count += 1;
    this.seen.set(signature, entry);
    this.prune();

    const report = describeError(error, entry.count, this.currentUrl());

    // ---- Guarantee 1: ALWAYS log. Never inside a condition, never after a `return`. ----
    console.error('[GlobalErrorHandler]', report, error);

    if (this.reporting) {
      // We are already inside a handleError call — this error came from the reporting path
      // below. It is logged (above), which is the guarantee; going further would recurse.
      return;
    }
    this.reporting = true;
    try {
      // ---- Guarantee 2: ALWAYS notify the monitor, repeats included. ----
      // Isolated in its OWN try/catch, deliberately: a monitoring adapter that throws (a bad
      // DSN, an SDK not yet initialised, a network stub in a test) must not take the toast down
      // with it. Letting one reporting channel's failure silence the other is precisely the
      // fault this class exists to prevent, reproduced one level down.
      try {
        this.monitor?.captureException(error, report);
      } catch (monitorFailure: unknown) {
        console.error('[GlobalErrorHandler] the monitoring adapter threw while reporting', monitorFailure);
      }

      // ---- Guarantee 3: toast, but only once per window per signature. ----
      this.maybeToast(error, report, entry);
    } catch (surfacingFailure: unknown) {
      // The toast path itself broke. The ORIGINAL error is already logged above and is the one
      // that matters; log this one too rather than letting it disappear.
      console.error('[GlobalErrorHandler] failed while surfacing an error', surfacingFailure);
    } finally {
      this.reporting = false;
    }
  }

  private maybeToast(error: unknown, report: ErrorReport, entry: { lastToastAt: number | null }): void {
    // A 401 already surfaced through SESSION_EXPIRED_NOTIFIER: the login page says "your session
    // expired", which is both more accurate and more actionable than "something went wrong".
    // Same policy as `reportQueryError`. It is still logged and still reported, above.
    if (isSessionExpiredError(error)) {
      return;
    }

    const now = Date.now();
    if (entry.lastToastAt !== null && now - entry.lastToastAt < DUPLICATE_ERROR_WINDOW_MS) {
      return;
    }
    entry.lastToastAt = now;

    this.messages.add({
      severity: 'error',
      summary: 'Something went wrong',
      detail: toastDetail(error, report),
      life: 8000,
    });
  }

  /** Drops the least recently toasted signatures once the map exceeds its cap. */
  private prune(): void {
    if (this.seen.size <= MAX_TRACKED_SIGNATURES) {
      return;
    }
    const oldestFirst = [...this.seen.entries()].sort((a, b) => (a[1].lastToastAt ?? 0) - (b[1].lastToastAt ?? 0));
    for (const [signature] of oldestFirst.slice(0, this.seen.size - MAX_TRACKED_SIGNATURES)) {
      this.seen.delete(signature);
    }
  }

  private currentUrl(): string | null {
    return typeof window === 'undefined' ? null : window.location.href;
  }
}

/**
 * User-facing copy. Plain language, no stack trace, no error class name — plus the `traceId` when
 * the API sent one, because "quote reference 7f3a…" is the difference between a support ticket
 * that can be traced to a server-side request and one that cannot.
 */
export function toastDetail(error: unknown, report: ErrorReport): string {
  const base = apiErrorMessage(error, 'Something went wrong. The problem has been reported.');
  return report.traceId === null ? base : `${base} Reference: ${report.traceId}`;
}

/**
 * Installs {@link GlobalErrorHandler} as the app's `ErrorHandler`. Call it in
 * `apps/web/src/app/app.config.ts`, alongside `provideBrowserGlobalErrorListeners()` — that
 * provider is what routes `window` `error`/`unhandledrejection` events into Angular's
 * `ErrorHandler`, so the two together are what make BOTH Angular-internal and window-level
 * failures visible. Neither is sufficient alone.
 */
export function provideGlobalErrorHandler(): EnvironmentProviders {
  return makeEnvironmentProviders([{ provide: ErrorHandler, useClass: GlobalErrorHandler }]);
}

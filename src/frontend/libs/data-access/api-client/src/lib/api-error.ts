/** Thrown by the envelope interceptor whenever a response fails or `ApiResponse.success` is false. */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly body: unknown,
    public readonly code?: string | null,
    /** Correlation id from the envelope — quote it in support-facing error copy. */
    public readonly traceId?: string | null,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

/**
 * True when `err` is a 409 `ApiError` — an optimistic-concurrency conflict. The server rejected
 * the write because the `rowVersion` sent no longer matches the row someone else already
 * updated. The ONLY correct recovery is to reload and let the user look at the current values.
 */
export function isConflictError(err: unknown): boolean {
  return err instanceof ApiError && err.status === 409;
}

/**
 * Extracts a typed conflict payload from a thrown `ApiError`, when the API returns one instead of
 * the updated resource. The envelope still wraps it (`success: false`, `data: <payload>`), so the
 * payload sits one level deeper than a plain `err.status === 409` check would suggest. Returns
 * `undefined` for anything that isn't a 409 carrying `data`, so it is always safe to try first.
 */
export function conflictData<T>(err: unknown): T | undefined {
  if (!(err instanceof ApiError) || err.status !== 409) {
    return undefined;
  }
  const body = err.body as { data?: T } | null | undefined;
  return body?.data ?? undefined;
}

/**
 * Unwraps a mutation error into a user-facing message. A 409 always gets the same distinct,
 * actionable copy — regardless of `fallback` — since "reload and look again" is the only correct
 * next step for a stale-record conflict; any other `ApiError` appends its status to `fallback`;
 * anything else (a network failure, an unexpected throw) returns `fallback` as-is. One shared
 * implementation so every `onError` handler in the app reports conflicts identically instead of
 * re-deriving slightly different wording per component.
 */
export function apiErrorMessage(err: unknown, fallback: string): string {
  if (isConflictError(err)) {
    return 'Someone else changed this record while you were editing. Reload the page to see the latest version, then re-apply your change.';
  }
  return err instanceof ApiError ? `${fallback} (${err.status}).` : fallback;
}

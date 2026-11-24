import { HttpErrorResponse } from '@angular/common/http';
import { ApiError } from '@aj-boilerplate/data-access/api-client';

function statusOf(error: unknown): number | null {
  return error instanceof ApiError || error instanceof HttpErrorResponse ? error.status : null;
}

/**
 * Whether a failed `/api/v1/me` is the server's own answer ("you have no access", 403) rather than
 * the server not answering (5xx, 503 from the gateway, network). Only the first is a verdict; the
 * second must never be read as "no capabilities", or an outage would look like a permissions
 * problem and send everyone to access-denied.
 */
export function isAuthoritativeCapabilityAnswer(error: unknown): boolean {
  return statusOf(error) === 403;
}

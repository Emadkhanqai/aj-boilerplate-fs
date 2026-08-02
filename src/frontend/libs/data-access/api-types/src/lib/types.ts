/**
 * API types for the sample `Item` slice.
 *
 * GENERATED FILE — DO NOT HAND-EDIT. Regenerate with `npm run generate:api`, which runs
 * `openapi-typescript` against the backend's OpenAPI document and overwrites this file. Editing
 * it by hand is how a frontend silently drifts from the contract it is supposed to honour.
 *
 * If you need a shape the API does not expose, add it to the API first. Never re-declare a
 * backend DTO anywhere else in the workspace: `@aj-boilerplate/data-access/api-types` is the
 * single place these live.
 *
 * (Checked in as hand-written declarations here only so the boilerplate compiles before you have
 * a backend running; the first `npm run generate:api` replaces the whole file.)
 */

/* ============================ Envelope ============================ */

/**
 * Every API response is wrapped in this envelope. `envelopeInterceptor`
 * (`@aj-boilerplate/data-access/api-client`) unwraps it centrally, so feature code only ever
 * sees `T` — and a `success: false` body becomes a thrown `ApiError` regardless of HTTP status.
 */
export interface ApiResponse<T> {
  success: boolean;
  data?: T | null;
  message: string | null;
  errors: string[] | null;
  code: string | null;
  timestamp: string;
  /** Correlation id for this request. Surface it in support-facing error detail. */
  traceId: string | null;
}

/** A page of results. `total` is the unfiltered-by-page count, for the pager. */
export interface PagedResponse<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

/* ============================ Items (sample slice) ============================ */

/** Lifecycle state of an item. */
export type ItemStatus = 'Draft' | 'Active' | 'Archived';

/** All valid `ItemStatus` values, for dropdown options. */
export const ITEM_STATUSES: readonly ItemStatus[] = ['Draft', 'Active', 'Archived'];

export interface ItemResponse {
  id: string;
  name: string;
  description: string | null;
  status: ItemStatus;
  /** ISO-8601 instant. */
  createdAt: string;
  /** ISO-8601 instant, or `null` when the item has never been updated. */
  updatedAt: string | null;
  /**
   * Optimistic-concurrency token. Send the value you read back on `PUT`; the server responds
   * `409 Conflict` if someone else has written the row since.
   */
  rowVersion: string;
}

export interface CreateItemRequest {
  name: string;
  description?: string | null;
  status?: ItemStatus;
}

export interface UpdateItemRequest {
  name: string;
  description?: string | null;
  status: ItemStatus;
  /** Must be the `rowVersion` from the item you loaded — see {@link ItemResponse.rowVersion}. */
  rowVersion: string;
}

/** Query string for `GET /api/v1/items`. */
export interface ItemListRequest {
  page?: number;
  pageSize?: number;
  search?: string;
}

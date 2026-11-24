/**
 * Hand-written contract types for the sample `Item` slice, kept stricter than the generated ones.
 *
 * `generated.ts` beside this file IS generated (`npm run generate:api`, from docs/api/openapi.json)
 * and must never be edited by hand. It is not used here for the request and response shapes because
 * the OpenAPI document declares no `required` properties and types `status` as a plain string, so
 * every generated field is optional/nullable and `ItemStatus` would be `string`. Swapping these
 * for them would push null checks into every component and lose the status union. Make the API
 * document nullability and enums (`[Required]`, an enum schema) first, then replace a type here
 * with `components['schemas'][...]` from `generated.ts`, one at a time.
 *
 * The `ApiResponse<T>` envelope and `ITEM_STATUSES` have no generated equivalent at all.
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
export const ITEM_STATUSES: readonly ItemStatus[] = [
  'Draft',
  'Active',
  'Archived',
];

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

/* ============================ Administration (users, groups, sign-in history, portals) ============================
 * Hand-written for the same reason as the Item slice above: the generated shapes are all optional
 * and nullable. Checked against docs/api/openapi.json (DirectoryUserResponse, DirectoryMemberResponse,
 * DirectoryGroupResponse, UserGroupsResponse, CreatedDirectoryUserResponse, MePortalsResponse,
 * AuthenticationSessionResponse, AuthenticationHistoryUserResponse). */

export interface DirectoryUser {
  subject: string;
  username: string;
  email?: string | null;
  firstName?: string | null;
  lastName?: string | null;
  enabled: boolean;
}

export interface DirectoryMember {
  user: DirectoryUser;
  groups: string[];
}

export interface DirectoryGroup {
  path: string;
  name: string;
  parentPath?: string | null;
}

export interface CreateDirectoryUserRequest {
  email: string;
  firstName?: string | null;
  lastName?: string | null;
  groupPath: string;
}

export interface CreatedDirectoryUser {
  user: DirectoryUser;
  groups: string[];
}

/** `PUT/GET /api/v1/admin/users/{subject}/groups`. `added`/`removed` describe the PUT's effect. */
export interface UserGroupsResult {
  groups: string[];
  added: string[];
  removed: string[];
}

export interface PortalLink {
  key: string;
  name: string;
  /** Where it lives; may be a relative path. */
  url: string;
}

export interface MePortals {
  portalKeys: string[];
  portals: PortalLink[];
}

/** `signedIn | user | sessionEnded | duration | result | endReason` — the API's `sortBy` vocabulary. */
export type AuthHistorySortBy = 'signedIn' | 'user' | 'sessionEnded' | 'duration' | 'result' | 'endReason';

export interface AuthenticationSession {
  id: string;
  userId?: string | null;
  userDisplayName?: string | null;
  signedInAtUtc: string;
  /** `null` while the session is still open. */
  endedAtUtc?: string | null;
  endReason?: string | null;
  endReasonDetail?: string | null;
  result?: string | null;
  durationSeconds?: number | null;
}

export interface AuthenticationHistoryUser {
  userId: string;
  userDisplayName?: string | null;
}

export interface AuthHistoryRequest {
  userId?: string;
  /** ISO instant, inclusive. */
  from?: string;
  /** ISO instant, exclusive. */
  to?: string;
  sortBy?: AuthHistorySortBy;
  sortDir?: 'asc' | 'desc';
  page?: number;
  pageSize?: number;
}

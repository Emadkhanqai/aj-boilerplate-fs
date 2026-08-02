import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import type { Observable } from 'rxjs';
import type {
  CreateItemRequest,
  ItemListRequest,
  ItemResponse,
  PagedResponse,
  UpdateItemRequest,
} from '@aj-boilerplate/data-access/api-types';

/** Base path. Versioned, always — an unversioned path is a breaking change waiting to happen. */
const ITEMS = '/api/v1/items';

function toListParams(request: ItemListRequest): HttpParams {
  let params = new HttpParams();
  if (request.page !== undefined) params = params.set('page', String(request.page));
  if (request.pageSize !== undefined) params = params.set('pageSize', String(request.pageSize));
  if (request.search !== undefined && request.search !== '') params = params.set('search', request.search);
  return params;
}

/**
 * SAMPLE FEATURE — delete this file along with `libs/feature-items`.
 *
 * The pattern every feature API service follows: one injectable per feature area, typed methods
 * over `HttpClient`, and NO manual envelope handling — `envelopeInterceptor` already unwrapped
 * `ApiResponse<T>` before this service sees the response, so the observable emits the DTO
 * directly. Errors arrive as `ApiError`; a `409` from `update()` is the optimistic-concurrency
 * conflict the UI must surface (see `apiErrorMessage`).
 */
@Injectable({ providedIn: 'root' })
export class ItemsApiService {
  private readonly http = inject(HttpClient);

  list(request: ItemListRequest = {}): Observable<PagedResponse<ItemResponse>> {
    return this.http.get<PagedResponse<ItemResponse>>(ITEMS, { params: toListParams(request) });
  }

  getById(id: string): Observable<ItemResponse> {
    return this.http.get<ItemResponse>(`${ITEMS}/${encodeURIComponent(id)}`);
  }

  create(request: CreateItemRequest): Observable<ItemResponse> {
    return this.http.post<ItemResponse>(ITEMS, request);
  }

  /** Responds `409 Conflict` when `request.rowVersion` is stale. */
  update(id: string, request: UpdateItemRequest): Observable<ItemResponse> {
    return this.http.put<ItemResponse>(`${ITEMS}/${encodeURIComponent(id)}`, request);
  }

  remove(id: string): Observable<void> {
    return this.http.delete<void>(`${ITEMS}/${encodeURIComponent(id)}`);
  }
}

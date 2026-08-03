import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { map } from 'rxjs/operators';
import type { Observable } from 'rxjs';

/** Base path. Versioned, always — an unversioned path is a breaking change waiting to happen. */
const FEATURES = '/api/v1/features';

/**
 * A "What's new" feature announcement, as returned by `GET /api/v1/features/unack`.
 *
 * DECLARED HERE ONLY TEMPORARILY. `@aj-boilerplate/data-access/api-types` is generated from the
 * backend's OpenAPI document by `npm run generate:api` and must never be hand-edited, and this
 * endpoint is not in that document yet. Once the backend publishes it: regenerate the types,
 * delete this interface, and import `FeatureAnnouncement` from
 * `@aj-boilerplate/data-access/api-types` instead. It must not survive as a second declaration
 * of a shape the contract already describes.
 *
 * `titleAr`/`bodyAr` are nullable: an announcement may ship English-only, and `LanguageService`
 * (`@aj-boilerplate/shared/util`) falls back rather than rendering a blank.
 */
export interface FeatureAnnouncement {
  id: string;
  /** Stable machine identifier for whoever authors the announcement. Never shown to users. */
  key: string;
  titleEn: string;
  titleAr: string | null;
  /** Light-markdown body — see `WhatsNewModalComponent` for the two conventions it understands. */
  bodyEn: string;
  bodyAr: string | null;
  /** Lower shows first when several announcements are pending at once. */
  displayOrder: number;
  createdAt: string;
}

/**
 * "What's new" announcements gateway.
 *
 *  - {@link unack} resolves which active announcements the current user has not dismissed yet
 *    AND whose page list matches the given URL path. `AppLayoutComponent` calls it on every
 *    route change; the server does all the filtering.
 *  - {@link ack} marks announcements as acknowledged for the current user. Idempotent
 *    server-side, so a double-dismiss is harmless.
 *
 * No manual envelope handling here, as with every service in this library — `envelopeInterceptor`
 * has already unwrapped `ApiResponse<T>` by the time these observables emit.
 */
@Injectable({ providedIn: 'root' })
export class FeatureAnnouncementsApiService {
  private readonly http = inject(HttpClient);

  /**
   * @param path the URL path the user is currently on. The server matches it against each
   * announcement's registered page prefixes (and normalises it first, so `..` segments cannot be
   * used to make an announcement fire on an unrelated route).
   */
  unack(path: string): Observable<FeatureAnnouncement[]> {
    return this.http
      .get<FeatureAnnouncement[] | null>(`${FEATURES}/unack`, {
        params: new HttpParams().set('path', path),
      })
      // An envelope with a null `data` unwraps to null; callers want "nothing pending", not a
      // null check at every call site.
      .pipe(map((list) => list ?? []));
  }

  /** Responds `204 No Content`. Already-acknowledged ids are silently skipped by the server. */
  ack(featureIds: string[]): Observable<void> {
    return this.http.post<void>(`${FEATURES}/ack`, { featureIds });
  }
}

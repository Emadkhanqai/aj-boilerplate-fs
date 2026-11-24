import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import type { Observable } from 'rxjs';
import type {
  AuthHistoryRequest,
  AuthenticationHistoryUser,
  AuthenticationSession,
  CreateDirectoryUserRequest,
  CreatedDirectoryUser,
  DirectoryGroup,
  DirectoryMember,
  DirectoryUser,
  MePortals,
  PagedResponse,
  UserGroupsResult,
} from '@aj-boilerplate/data-access/api-types';

const ADMIN = '/api/v1/admin';

function historyParams(r: AuthHistoryRequest): HttpParams {
  let params = new HttpParams();
  for (const [key, value] of Object.entries(r)) {
    if (value !== undefined && value !== '') params = params.set(key, String(value));
  }
  return params;
}

/**
 * Users / groups / sign-in history (all `canAdminister`), plus `GET /me/portals`. The envelope is
 * already unwrapped by `envelopeInterceptor`. The server answers an empty `users` list for a blank
 * `q` — callers wanting everyone use {@link members}.
 */
@Injectable({ providedIn: 'root' })
export class AdminApiService {
  private readonly http = inject(HttpClient);

  searchUsers(q: string, limit = 50): Observable<{ users: DirectoryUser[] }> {
    return this.http.get<{ users: DirectoryUser[] }>(`${ADMIN}/users`, {
      params: new HttpParams().set('q', q).set('limit', String(limit)),
    });
  }

  members(): Observable<{ members: DirectoryMember[] }> {
    return this.http.get<{ members: DirectoryMember[] }>(`${ADMIN}/members`);
  }

  groups(): Observable<{ groups: DirectoryGroup[] }> {
    return this.http.get<{ groups: DirectoryGroup[] }>(`${ADMIN}/groups`);
  }

  createUser(request: CreateDirectoryUserRequest): Observable<CreatedDirectoryUser> {
    return this.http.post<CreatedDirectoryUser>(`${ADMIN}/users`, request);
  }

  userGroups(subject: string): Observable<UserGroupsResult> {
    return this.http.get<UserGroupsResult>(`${ADMIN}/users/${encodeURIComponent(subject)}/groups`);
  }

  setUserGroups(subject: string, groups: string[]): Observable<UserGroupsResult> {
    return this.http.put<UserGroupsResult>(`${ADMIN}/users/${encodeURIComponent(subject)}/groups`, { groups });
  }

  /** 204 — resolves with `null` after the envelope interceptor. */
  setEnabled(subject: string, enabled: boolean): Observable<void> {
    return this.http.post<void>(`${ADMIN}/users/${encodeURIComponent(subject)}/${enabled ? 'enable' : 'disable'}`, null);
  }

  refreshSessions(subject: string): Observable<void> {
    return this.http.post<void>(`${ADMIN}/users/${encodeURIComponent(subject)}/refresh`, null);
  }

  authHistory(request: AuthHistoryRequest): Observable<PagedResponse<AuthenticationSession>> {
    return this.http.get<PagedResponse<AuthenticationSession>>(`${ADMIN}/authentication-history`, {
      params: historyParams(request),
    });
  }

  authHistoryUsers(): Observable<AuthenticationHistoryUser[]> {
    return this.http.get<AuthenticationHistoryUser[]>(`${ADMIN}/authentication-history/users`);
  }

  myPortals(): Observable<MePortals> {
    return this.http.get<MePortals>('/api/v1/me/portals');
  }
}

import { TestBed } from '@angular/core/testing';
import type { EnvironmentProviders, Provider } from '@angular/core';
import { HttpClient, HttpHeaders, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { firstValueFrom } from 'rxjs';
import {
  ACCESS_REVOKED_OBSERVER,
  FORBIDDEN_OBSERVER,
  accessRevokedInterceptor,
  isAccessRevokedPage,
  isAccessRevokedResponse,
  resetAccessRevokedPageForTests,
} from './access-revoked';

describe('isAccessRevokedResponse', () => {
  const revoked = new HttpHeaders({ 'X-Usp-Access': 'revoked' });

  it('is true only for 403 AND the revoked header', () => {
    expect(isAccessRevokedResponse(403, revoked)).toBe(true);
  });

  it('is false for a bare 403 (an ordinary "not allowed")', () => {
    expect(isAccessRevokedResponse(403, new HttpHeaders())).toBe(false);
    expect(isAccessRevokedResponse(403, undefined)).toBe(false);
  });

  it('is false when the header is on any other status', () => {
    expect(isAccessRevokedResponse(401, revoked)).toBe(false);
    expect(isAccessRevokedResponse(200, revoked)).toBe(false);
  });

  it('is false for a different header value', () => {
    expect(isAccessRevokedResponse(403, new HttpHeaders({ 'X-Usp-Access': 'denied' }))).toBe(false);
  });
});

describe('accessRevokedInterceptor', () => {
  let http: HttpClient;
  let httpTesting: HttpTestingController;
  let revokedObserver: ReturnType<typeof vi.fn>;
  let forbiddenObserver: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    resetAccessRevokedPageForTests();
    revokedObserver = vi.fn();
    forbiddenObserver = vi.fn();
    const providers: (Provider | EnvironmentProviders)[] = [
      provideHttpClient(withInterceptors([accessRevokedInterceptor])),
      provideHttpClientTesting(),
      { provide: ACCESS_REVOKED_OBSERVER, useValue: revokedObserver },
      { provide: FORBIDDEN_OBSERVER, useValue: forbiddenObserver },
    ];
    TestBed.configureTestingModule({ providers });
    http = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpTesting.verify();
    resetAccessRevokedPageForTests();
  });

  async function fail(url: string, status: number, headers: Record<string, string> = {}): Promise<void> {
    const result = firstValueFrom(http.get(url));
    httpTesting.expectOne(url).flush('x', { status, statusText: 'x', headers });
    await result.catch(() => undefined);
  }

  it('opens the dialog (observer + latch) on 403 with X-Usp-Access: revoked', async () => {
    await fail('/api/v1/items', 403, { 'X-Usp-Access': 'revoked' });

    expect(revokedObserver).toHaveBeenCalledTimes(1);
    expect(forbiddenObserver).not.toHaveBeenCalled();
    expect(isAccessRevokedPage()).toBe(true);
  });

  it('does NOT open it for a bare 403, but asks for a roles re-check', async () => {
    await fail('/api/v1/items', 403);

    expect(revokedObserver).not.toHaveBeenCalled();
    expect(isAccessRevokedPage()).toBe(false);
    expect(forbiddenObserver).toHaveBeenCalledTimes(1);
  });

  it('does not ask for a re-check when the 403 is the profile read itself (that would loop)', async () => {
    await fail('/api/v1/me?x=1', 403);

    expect(forbiddenObserver).not.toHaveBeenCalled();
  });

  it('ignores every other failure', async () => {
    await fail('/api/v1/items', 500);
    await fail('/api/v1/items', 401, { 'X-Usp-Access': 'revoked' });

    expect(revokedObserver).not.toHaveBeenCalled();
    expect(forbiddenObserver).not.toHaveBeenCalled();
  });
});

import { TestBed } from '@angular/core/testing';
import type { EnvironmentProviders, Provider } from '@angular/core';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it, afterEach, vi } from 'vitest';
import { firstValueFrom } from 'rxjs';
import { authInterceptor } from './auth-interceptor';
import { AUTH_TOKEN_PROVIDER } from './auth-token';
import { SESSION_EXPIRED_NOTIFIER, TOKEN_REFRESHER, isSessionExpiredError } from './session-expiry';
import { envelopeInterceptor } from './envelope-interceptor';

const UNAUTHORIZED_ENVELOPE = {
  success: false,
  data: null,
  message: 'Unauthorized.',
  errors: null,
  code: 'UNAUTHORIZED',
  timestamp: '2026-07-16T00:00:00Z',
  traceId: null,
};

/** Let any pending microtasks (the refresh Promise, its downstream switchMap) settle. */
function flushMicrotasks(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

describe('authInterceptor', () => {
  let httpClient: HttpClient;
  let httpTesting: HttpTestingController;

  function setup(
    tokenProvider: () => string | null,
    opts: { refresher?: () => Promise<string | null>; notifier?: () => void } = {},
  ): void {
    const providers: (Provider | EnvironmentProviders)[] = [
      provideHttpClient(withInterceptors([authInterceptor])),
      provideHttpClientTesting(),
      { provide: AUTH_TOKEN_PROVIDER, useValue: tokenProvider },
    ];
    if (opts.refresher !== undefined) {
      providers.push({ provide: TOKEN_REFRESHER, useValue: opts.refresher });
    }
    if (opts.notifier !== undefined) {
      providers.push({ provide: SESSION_EXPIRED_NOTIFIER, useValue: opts.notifier });
    }
    TestBed.configureTestingModule({ providers });
    httpClient = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  }

  /** Mirrors app.config.ts's real interceptor order — needed so a 401 body arrives as the
   * `ApiError` the retry logic keys off, not a raw `HttpErrorResponse`. */
  function setupWithEnvelope(
    tokenProvider: () => string | null,
    opts: { refresher?: () => Promise<string | null>; notifier?: () => void } = {},
  ): void {
    const providers: (Provider | EnvironmentProviders)[] = [
      provideHttpClient(withInterceptors([authInterceptor, envelopeInterceptor])),
      provideHttpClientTesting(),
      { provide: AUTH_TOKEN_PROVIDER, useValue: tokenProvider },
    ];
    if (opts.refresher !== undefined) {
      providers.push({ provide: TOKEN_REFRESHER, useValue: opts.refresher });
    }
    if (opts.notifier !== undefined) {
      providers.push({ provide: SESSION_EXPIRED_NOTIFIER, useValue: opts.notifier });
    }
    TestBed.configureTestingModule({ providers });
    httpClient = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  }

  afterEach(() => {
    httpTesting.verify();
  });

  it('attaches Authorization: Bearer <token> when a token is present', () => {
    setup(() => 'abc123');
    httpClient.get('/api/v1/items').subscribe();
    const req = httpTesting.expectOne('/api/v1/items');
    expect(req.request.headers.get('Authorization')).toBe('Bearer abc123');
    req.flush({});
  });

  it('omits the Authorization header when the token provider returns null', () => {
    setup(() => null);
    httpClient.get('/api/v1/items').subscribe();
    const req = httpTesting.expectOne('/api/v1/items');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({});
  });


  describe('refresh-on-401', () => {
    it('retries once with a refreshed token after a 401, then resolves with the retried response', async () => {
      const refresher = vi.fn(async () => 'new-token');
      setupWithEnvelope(() => 'old-token', { refresher });

      const promise = firstValueFrom(httpClient.get('/api/v1/items'));

      const first = httpTesting.expectOne('/api/v1/items');
      expect(first.request.headers.get('Authorization')).toBe('Bearer old-token');
      first.flush(UNAUTHORIZED_ENVELOPE, { status: 401, statusText: 'Unauthorized' });

      await flushMicrotasks();

      const retried = httpTesting.expectOne('/api/v1/items');
      expect(retried.request.headers.get('Authorization')).toBe('Bearer new-token');
      retried.flush({
        success: true,
        data: { ok: true },
        message: null,
        errors: null,
        code: null,
        timestamp: '2026-07-16T00:00:00Z',
        traceId: null,
      });

      await expect(promise).resolves.toEqual({ ok: true });
      expect(refresher).toHaveBeenCalledOnce();
    });

    it('notifies session-expired and does not retry when no refresher is bound', async () => {
      const notifier = vi.fn();
      setupWithEnvelope(() => 'old-token', { notifier });

      const promise = firstValueFrom(httpClient.get('/api/v1/items'));
      const req = httpTesting.expectOne('/api/v1/items');
      req.flush(UNAUTHORIZED_ENVELOPE, { status: 401, statusText: 'Unauthorized' });

      await expect(promise).rejects.toThrow();
      expect(notifier).toHaveBeenCalledOnce();
    });

    it('notifies session-expired and does not retry when refreshing resolves null', async () => {
      const notifier = vi.fn();
      const refresher = vi.fn(async () => null);
      setupWithEnvelope(() => 'old-token', { refresher, notifier });

      const promise = firstValueFrom(httpClient.get('/api/v1/items'));
      const req = httpTesting.expectOne('/api/v1/items');
      req.flush(UNAUTHORIZED_ENVELOPE, { status: 401, statusText: 'Unauthorized' });

      await expect(promise).rejects.toThrow();
      expect(notifier).toHaveBeenCalledOnce();
    });

    it('notifies session-expired when the retried request also 401s', async () => {
      const notifier = vi.fn();
      const refresher = vi.fn(async () => 'new-token');
      setupWithEnvelope(() => 'old-token', { refresher, notifier });

      const promise = firstValueFrom(httpClient.get('/api/v1/items'));
      const first = httpTesting.expectOne('/api/v1/items');
      first.flush(UNAUTHORIZED_ENVELOPE, { status: 401, statusText: 'Unauthorized' });

      await flushMicrotasks();

      const retried = httpTesting.expectOne('/api/v1/items');
      retried.flush(UNAUTHORIZED_ENVELOPE, { status: 401, statusText: 'Unauthorized' });

      await expect(promise).rejects.toThrow();
      expect(notifier).toHaveBeenCalledOnce();
    });

    it('marks the rethrown error via isSessionExpiredError when no refresher is bound', async () => {
      setupWithEnvelope(() => 'old-token', { notifier: vi.fn() });

      const promise = firstValueFrom(httpClient.get('/api/v1/items'));
      const req = httpTesting.expectOne('/api/v1/items');
      req.flush(UNAUTHORIZED_ENVELOPE, { status: 401, statusText: 'Unauthorized' });

      const err = await promise.catch((e: unknown) => e);
      expect(isSessionExpiredError(err)).toBe(true);
    });

    it('marks the rethrown error via isSessionExpiredError when the retried request also 401s', async () => {
      const refresher = vi.fn(async () => 'new-token');
      setupWithEnvelope(() => 'old-token', { refresher, notifier: vi.fn() });

      const promise = firstValueFrom(httpClient.get('/api/v1/items'));
      const first = httpTesting.expectOne('/api/v1/items');
      first.flush(UNAUTHORIZED_ENVELOPE, { status: 401, statusText: 'Unauthorized' });

      await flushMicrotasks();

      const retried = httpTesting.expectOne('/api/v1/items');
      retried.flush(UNAUTHORIZED_ENVELOPE, { status: 401, statusText: 'Unauthorized' });

      const err = await promise.catch((e: unknown) => e);
      expect(isSessionExpiredError(err)).toBe(true);
    });

    it('does not mark a 401 with no token attached as a session-expiry error', async () => {
      const refresher = vi.fn(async () => 'new-token');
      setupWithEnvelope(() => null, { refresher, notifier: vi.fn() });

      const promise = firstValueFrom(httpClient.get('/api/v1/items'));
      const req = httpTesting.expectOne('/api/v1/items');
      req.flush(UNAUTHORIZED_ENVELOPE, { status: 401, statusText: 'Unauthorized' });

      const err = await promise.catch((e: unknown) => e);
      expect(isSessionExpiredError(err)).toBe(false);
    });

    it('does not attempt refresh or notify for a 401 with no token attached', async () => {
      const notifier = vi.fn();
      const refresher = vi.fn(async () => 'new-token');
      setupWithEnvelope(() => null, { refresher, notifier });

      const promise = firstValueFrom(httpClient.get('/api/v1/items'));
      const req = httpTesting.expectOne('/api/v1/items');
      expect(req.request.headers.has('Authorization')).toBe(false);
      req.flush(UNAUTHORIZED_ENVELOPE, { status: 401, statusText: 'Unauthorized' });

      await expect(promise).rejects.toThrow();
      expect(refresher).not.toHaveBeenCalled();
      expect(notifier).not.toHaveBeenCalled();
    });
  });
});

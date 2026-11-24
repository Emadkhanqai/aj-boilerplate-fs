import { TestBed } from '@angular/core/testing';
import type { EnvironmentProviders, Provider } from '@angular/core';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { firstValueFrom } from 'rxjs';
import { authInterceptor } from './auth-interceptor';
import { accessRevokedInterceptor, resetAccessRevokedPageForTests } from './access-revoked';
import { AMBIENT_SESSION_ACTIVE, SESSION_EXPIRED_NOTIFIER, isSessionExpiredError } from './session-expiry';
import { envelopeInterceptor } from './envelope-interceptor';

const envelope = (message: string, code: string) => ({
  success: false,
  data: null,
  message,
  errors: null,
  code,
  timestamp: '2026-07-16T00:00:00Z',
  traceId: null,
});

describe('authInterceptor (gateway: the browser holds no token)', () => {
  let http: HttpClient;
  let httpTesting: HttpTestingController;
  let notifier: ReturnType<typeof vi.fn>;

  /** The REAL interceptor order from app.config.ts: auth sees the unwrapped ApiError, the revoked
   * observer sees the raw HttpErrorResponse and its headers. */
  function setup(opts: { ambient?: boolean } = {}): void {
    notifier = vi.fn();
    const providers: (Provider | EnvironmentProviders)[] = [
      provideHttpClient(withInterceptors([authInterceptor, envelopeInterceptor, accessRevokedInterceptor])),
      provideHttpClientTesting(),
      { provide: SESSION_EXPIRED_NOTIFIER, useValue: notifier },
      { provide: AMBIENT_SESSION_ACTIVE, useValue: () => opts.ambient ?? true },
    ];
    TestBed.configureTestingModule({ providers });
    http = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  }

  beforeEach(() => resetAccessRevokedPageForTests());
  afterEach(() => {
    httpTesting.verify();
    resetAccessRevokedPageForTests();
  });

  it('attaches NO credential: no Authorization header, whatever the app holds', async () => {
    setup();

    const result = firstValueFrom(http.get('/api/v1/items'));
    const req = httpTesting.expectOne('/api/v1/items');
    req.flush({ success: true, data: [], message: null, errors: null, code: null, timestamp: '', traceId: null });
    await result;

    expect(req.request.headers.has('Authorization')).toBe(false);
    expect(req.request.headers.keys()).toEqual([]);
  });

  it('on a 401 for a live session: notifies once, marks the error as already surfaced, and still rejects', async () => {
    setup();

    const result = firstValueFrom(http.get('/api/v1/items'));
    httpTesting.expectOne('/api/v1/items').flush(envelope('Unauthorized.', 'UNAUTHORIZED'), { status: 401, statusText: 'Unauthorized' });
    const err = await result.catch((e: unknown) => e);

    expect(notifier).toHaveBeenCalledTimes(1);
    expect(isSessionExpiredError(err)).toBe(true);
    expect((err as { status: number }).status).toBe(401);
  });

  it('does NOT treat a 401 as an expired session when none is active (an anonymous visitor on /login)', async () => {
    setup({ ambient: false });

    const result = firstValueFrom(http.get('/api/v1/me'));
    httpTesting.expectOne('/api/v1/me').flush(envelope('Unauthorized.', 'UNAUTHORIZED'), { status: 401, statusText: 'Unauthorized' });
    const err = await result.catch((e: unknown) => e);

    expect(notifier).not.toHaveBeenCalled();
    expect(isSessionExpiredError(err)).toBe(false);
  });

  it.each([403, 500, 503])('never turns a %i into a login redirect: only a 401 may', async (status) => {
    setup();

    const result = firstValueFrom(http.get('/api/v1/items'));
    httpTesting.expectOne('/api/v1/items').flush(envelope('nope', 'X'), { status, statusText: 'x' });
    await result.catch(() => undefined);

    expect(notifier).not.toHaveBeenCalled();
  });

  describe('while the access-revoked dialog is open', () => {
    async function revoke(): Promise<void> {
      const first = firstValueFrom(http.get('/api/v1/items'));
      httpTesting
        .expectOne('/api/v1/items')
        .flush(envelope('Forbidden.', 'FORBIDDEN'), { status: 403, statusText: 'Forbidden', headers: { 'X-Usp-Access': 'revoked' } });
      await first.catch(() => undefined);
    }

    it('suppresses the 401 redirect that would tear the dialog down', async () => {
      setup();
      await revoke();

      const result = firstValueFrom(http.get('/api/v1/other'));
      httpTesting.expectOne('/api/v1/other').flush(envelope('Unauthorized.', 'UNAUTHORIZED'), { status: 401, statusText: 'Unauthorized' });
      const err = await result.catch((e: unknown) => e);

      expect(notifier).not.toHaveBeenCalled();
      expect(isSessionExpiredError(err)).toBe(true); // marked: no "Something went wrong" toast on top of the dialog
    });

    it('control: the same 401 DOES notify when no revoked verdict has been seen', async () => {
      setup();

      const result = firstValueFrom(http.get('/api/v1/other'));
      httpTesting.expectOne('/api/v1/other').flush(envelope('Unauthorized.', 'UNAUTHORIZED'), { status: 401, statusText: 'Unauthorized' });
      await result.catch(() => undefined);

      expect(notifier).toHaveBeenCalledTimes(1);
    });
  });
});

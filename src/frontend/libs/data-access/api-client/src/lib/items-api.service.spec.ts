import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { firstValueFrom } from 'rxjs';
import { ItemsApiService } from './items-api.service';
import { envelopeInterceptor } from './envelope-interceptor';
import { ApiError, isConflictError } from './api-error';

function envelope<T>(data: T): Record<string, unknown> {
  return {
    success: true,
    data,
    message: null,
    errors: null,
    code: null,
    timestamp: '2026-01-01T00:00:00Z',
    traceId: 'trace-1',
  };
}

describe('ItemsApiService', () => {
  let api: ItemsApiService;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([envelopeInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    api = TestBed.inject(ItemsApiService);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  it('requests the versioned list endpoint and unwraps the envelope', async () => {
    const promise = firstValueFrom(api.list({ page: 2, pageSize: 20, search: 'wid' }));

    const req = httpTesting.expectOne(
      (r) => r.url === '/api/v1/items' && r.params.get('page') === '2' && r.params.get('search') === 'wid',
    );
    expect(req.request.method).toBe('GET');
    req.flush(envelope({ items: [], total: 0, page: 2, pageSize: 20 }));

    await expect(promise).resolves.toEqual({ items: [], total: 0, page: 2, pageSize: 20 });
  });

  it('omits empty optional query params', () => {
    api.list({ search: '' }).subscribe();

    const req = httpTesting.expectOne((r) => r.url === '/api/v1/items');
    expect(req.request.params.keys()).toEqual([]);
    req.flush(envelope({ items: [], total: 0, page: 1, pageSize: 20 }));
  });

  it('url-encodes the id on read', () => {
    api.getById('a/b').subscribe();

    httpTesting.expectOne('/api/v1/items/a%2Fb').flush(envelope(null));
  });

  it('surfaces a stale rowVersion as a 409 ApiError', async () => {
    const promise = firstValueFrom(
      api.update('1', { name: 'n', description: null, status: 'Active', rowVersion: 'old' }),
    ).catch((err: unknown) => err);

    httpTesting.expectOne('/api/v1/items/1').flush(
      {
        success: false,
        data: null,
        message: 'The record was modified by another user.',
        errors: null,
        code: 'CONFLICT',
        timestamp: '2026-01-01T00:00:00Z',
        traceId: 'trace-9',
      },
      { status: 409, statusText: 'Conflict' },
    );

    const err = await promise;
    expect(err).toBeInstanceOf(ApiError);
    expect(isConflictError(err)).toBe(true);
    expect((err as ApiError).traceId).toBe('trace-9');
  });

  it('deletes via the versioned endpoint', () => {
    api.remove('7').subscribe();

    const req = httpTesting.expectOne('/api/v1/items/7');
    expect(req.request.method).toBe('DELETE');
    req.flush(null, { status: 204, statusText: 'No Content' });
  });
});

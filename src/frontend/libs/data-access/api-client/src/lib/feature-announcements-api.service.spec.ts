import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { firstValueFrom } from 'rxjs';
import { FeatureAnnouncementsApiService } from './feature-announcements-api.service';
import type { FeatureAnnouncement } from './feature-announcements-api.service';
import { envelopeInterceptor } from './envelope-interceptor';
import { ApiError } from './api-error';

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

function announcement(overrides: Partial<FeatureAnnouncement> = {}): FeatureAnnouncement {
  return {
    id: 'f-1',
    key: 'sample-v1',
    titleEn: 'Something new',
    titleAr: null,
    bodyEn: 'A body.',
    bodyAr: null,
    displayOrder: 0,
    createdAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('FeatureAnnouncementsApiService', () => {
  let api: FeatureAnnouncementsApiService;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([envelopeInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    api = TestBed.inject(FeatureAnnouncementsApiService);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  it('requests the versioned unack endpoint with the current path and unwraps the envelope', async () => {
    const promise = firstValueFrom(api.unack('/items/new'));

    const req = httpTesting.expectOne(
      (r) => r.url === '/api/v1/features/unack' && r.params.get('path') === '/items/new',
    );
    expect(req.request.method).toBe('GET');
    req.flush(envelope([announcement()]));

    await expect(promise).resolves.toEqual([announcement()]);
  });

  it('sends the path verbatim, including the root path', () => {
    api.unack('/').subscribe();

    const req = httpTesting.expectOne((r) => r.url === '/api/v1/features/unack');
    expect(req.request.params.get('path')).toBe('/');
    req.flush(envelope([]));
  });

  it('normalises a null envelope payload to an empty array', async () => {
    const promise = firstValueFrom(api.unack('/'));

    httpTesting.expectOne((r) => r.url === '/api/v1/features/unack').flush(envelope(null));

    await expect(promise).resolves.toEqual([]);
  });

  it('posts every acknowledged id in a single request', () => {
    api.ack(['f-1', 'f-2']).subscribe();

    const req = httpTesting.expectOne('/api/v1/features/ack');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ featureIds: ['f-1', 'f-2'] });
    req.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('surfaces a failed acknowledgement as an ApiError so the caller can ignore it deliberately', async () => {
    const promise = firstValueFrom(api.ack(['f-1'])).catch((err: unknown) => err);

    httpTesting.expectOne('/api/v1/features/ack').flush(
      {
        success: false,
        data: null,
        message: 'Nope.',
        errors: null,
        code: 'SERVER_ERROR',
        timestamp: '2026-01-01T00:00:00Z',
        traceId: 'trace-5',
      },
      { status: 500, statusText: 'Server Error' },
    );

    const err = await promise;
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).traceId).toBe('trace-5');
  });
});

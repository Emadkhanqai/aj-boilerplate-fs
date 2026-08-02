import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it, beforeEach, afterEach } from 'vitest';
import { envelopeInterceptor } from './envelope-interceptor';
import { ApiError } from './api-error';

describe('envelopeInterceptor', () => {
  let httpClient: HttpClient;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([envelopeInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    httpClient = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpTesting.verify();
  });

  it('unwraps ApiResponse.data on success', () => {
    let result: unknown;
    httpClient.get('/api/v1/items/1').subscribe((body) => {
      result = body;
    });
    httpTesting.expectOne('/api/v1/items/1').flush({
      success: true,
      data: { id: 1, title: 'Ramadan Campaign' },
      message: null,
      errors: null,
      code: null,
      timestamp: '2026-07-13T00:00:00Z',
      traceId: 't-1',
    });
    expect(result).toEqual({ id: 1, title: 'Ramadan Campaign' });
  });

  it('throws ApiError when the envelope reports success: false, even on HTTP 200', () => {
    let caught: unknown;
    httpClient.get('/api/v1/items/1').subscribe({
      error: (err: unknown) => {
        caught = err;
      },
    });
    httpTesting.expectOne('/api/v1/items/1').flush(
      {
        success: false,
        data: null,
        message: 'Item not found.',
        errors: ['Item not found.'],
        code: 'NOT_FOUND',
        timestamp: '2026-07-13T00:00:00Z',
        traceId: 't-2',
      },
      { status: 200, statusText: 'OK' },
    );
    expect(caught).toBeInstanceOf(ApiError);
    expect((caught as ApiError).message).toBe('Item not found.');
    expect((caught as ApiError).code).toBe('NOT_FOUND');
  });

  it('throws ApiError on a non-2xx HTTP status, preserving the envelope message', () => {
    let caught: unknown;
    httpClient.get('/api/v1/items/999').subscribe({
      error: (err: unknown) => {
        caught = err;
      },
    });
    httpTesting.expectOne('/api/v1/items/999').flush(
      {
        success: false,
        data: null,
        message: 'Not found.',
        errors: null,
        code: 'NOT_FOUND',
        timestamp: '2026-07-13T00:00:00Z',
        traceId: 't-3',
      },
      { status: 404, statusText: 'Not Found' },
    );
    expect(caught).toBeInstanceOf(ApiError);
    expect((caught as ApiError).status).toBe(404);
  });

  it('passes a Blob response body through unchanged, without attempting to unwrap it as an envelope', () => {
    let result: unknown;
    httpClient.get('/api/v1/items/export', { responseType: 'blob' }).subscribe((body) => {
      result = body;
    });
    const fakeBlob = new Blob(['%PDF-1.4 fake pdf bytes'], { type: 'application/pdf' });
    httpTesting.expectOne('/api/v1/items/export').flush(fakeBlob);
    expect(result).toBeInstanceOf(Blob);
    expect(result).toBe(fakeBlob);
  });
});

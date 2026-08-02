import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { MessageService } from 'primeng/api';
import { ApiError, markSessionExpired } from '@aj-boilerplate/data-access/api-client';
import { HttpErrorResponse } from '@angular/common/http';
import { GLOBAL_ERROR_TOAST_SUPPRESSED, QUERY_CLIENT, reportQueryError, retryPolicy } from './query-error-toasts';

function messageServiceStub(): MessageService {
  return { add: vi.fn() } as unknown as MessageService;
}

describe('reportQueryError', () => {
  it('adds an error toast for a generic Error with the generic fallback message', () => {
    const messageService = messageServiceStub();

    reportQueryError(messageService, new Error('boom'));

    expect(messageService.add).toHaveBeenCalledExactlyOnceWith({
      severity: 'error',
      summary: 'Something went wrong',
      detail: 'Something went wrong. Please try again.',
      life: 6000,
    });
  });

  it('adds an error toast for an ApiError, formatting detail via apiErrorMessage', () => {
    const messageService = messageServiceStub();
    const error = new ApiError(502, 'Bad gateway', null, null);

    reportQueryError(messageService, error);

    expect(messageService.add).toHaveBeenCalledExactlyOnceWith({
      severity: 'error',
      summary: 'Something went wrong',
      detail: 'Something went wrong. Please try again. (502).',
      life: 6000,
    });
  });

  it('gives a 409 conflict ApiError its distinct actionable copy', () => {
    const messageService = messageServiceStub();
    const error = new ApiError(409, 'Conflict', null, null);

    reportQueryError(messageService, error);

    expect(messageService.add).toHaveBeenCalledExactlyOnceWith({
      severity: 'error',
      summary: 'Something went wrong',
      detail:
        'Someone else changed this record while you were editing. Reload the page to see the latest version, then re-apply your change.',
      life: 6000,
    });
  });

  it('does not toast when the error was already surfaced via SESSION_EXPIRED_NOTIFIER', () => {
    const messageService = messageServiceStub();
    const error = new ApiError(401, 'Unauthorized', null, null);
    markSessionExpired(error);

    reportQueryError(messageService, error);

    expect(messageService.add).not.toHaveBeenCalled();
  });

  it('does not toast when the mutation meta is tagged GLOBAL_ERROR_TOAST_SUPPRESSED', () => {
    const messageService = messageServiceStub();

    reportQueryError(messageService, new Error('boom'), GLOBAL_ERROR_TOAST_SUPPRESSED);

    expect(messageService.add).not.toHaveBeenCalled();
  });

  it('still toasts a plain 401 (not marked session-expired) — e.g. an anonymous request failure', () => {
    const messageService = messageServiceStub();
    const error = new ApiError(401, 'Unauthorized', null, null);

    reportQueryError(messageService, error);

    expect(messageService.add).toHaveBeenCalledOnce();
  });
});

describe('QUERY_CLIENT', () => {
  it('wires its queryCache and mutationCache onError to reportQueryError via the injected MessageService', () => {
    const messageService = messageServiceStub();
    TestBed.configureTestingModule({ providers: [{ provide: MessageService, useValue: messageService }] });

    const client = TestBed.runInInjectionContext(() => TestBed.inject(QUERY_CLIENT));

    const queryOnError = client.getQueryCache().config.onError;
    const mutationOnError = client.getMutationCache().config.onError;
    expect(queryOnError).toBeDefined();
    expect(mutationOnError).toBeDefined();

    queryOnError?.(new Error('query failed'), { meta: undefined } as never);
    expect(messageService.add).toHaveBeenCalledWith(
      expect.objectContaining({ detail: 'Something went wrong. Please try again.' }),
    );

    (messageService.add as ReturnType<typeof vi.fn>).mockClear();
    mutationOnError?.(new Error('mutation failed'), undefined, undefined, { meta: GLOBAL_ERROR_TOAST_SUPPRESSED } as never, undefined as never);
    expect(messageService.add).not.toHaveBeenCalled();
  });

  it('suppresses a query error the same way a mutation error is suppressed, given a query.meta tagged GLOBAL_ERROR_TOAST_SUPPRESSED', () => {
    const messageService = messageServiceStub();
    TestBed.configureTestingModule({ providers: [{ provide: MessageService, useValue: messageService }] });

    const client = TestBed.runInInjectionContext(() => TestBed.inject(QUERY_CLIENT));
    const queryOnError = client.getQueryCache().config.onError;

    queryOnError?.(new Error('query failed'), { meta: GLOBAL_ERROR_TOAST_SUPPRESSED } as never);

    expect(messageService.add).not.toHaveBeenCalled();
  });
});

describe('retryPolicy', () => {
  it('never retries a 4xx client error, even on the first failure', () => {
    expect(retryPolicy(0, new HttpErrorResponse({ status: 400 }))).toBe(false);
    expect(retryPolicy(0, new HttpErrorResponse({ status: 404 }))).toBe(false);
    expect(retryPolicy(0, new HttpErrorResponse({ status: 422 }))).toBe(false);
  });

  it('retries a 5xx up to 2 times, then stops', () => {
    expect(retryPolicy(0, new HttpErrorResponse({ status: 500 }))).toBe(true);
    expect(retryPolicy(1, new HttpErrorResponse({ status: 503 }))).toBe(true);
    expect(retryPolicy(2, new HttpErrorResponse({ status: 500 }))).toBe(false);
  });

  it('retries a non-HTTP error (dropped connection) up to 2 times', () => {
    expect(retryPolicy(0, new Error('network down'))).toBe(true);
    expect(retryPolicy(2, new Error('network down'))).toBe(false);
  });

  it('is wired as the QueryClient default retry option', () => {
    const messageService = messageServiceStub();
    TestBed.configureTestingModule({ providers: [{ provide: MessageService, useValue: messageService }] });
    const client = TestBed.runInInjectionContext(() => TestBed.inject(QUERY_CLIENT));
    expect(client.getDefaultOptions().queries?.retry).toBe(retryPolicy);
  });
});

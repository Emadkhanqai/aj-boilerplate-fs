import { TestBed } from '@angular/core/testing';
import { ErrorHandler } from '@angular/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MessageService } from 'primeng/api';
import { ApiError, markSessionExpired } from '@aj-boilerplate/data-access/api-client';
import {
  DUPLICATE_ERROR_WINDOW_MS,
  ERROR_MONITOR,
  GlobalErrorHandler,
  describeError,
  errorSignature,
  provideGlobalErrorHandler,
  type ErrorMonitor,
  type ErrorReport,
} from './global-error-handler';

function messageServiceStub(): MessageService {
  return { add: vi.fn() } as unknown as MessageService;
}

/**
 * Builds the handler under test with stubbed collaborators, and returns the three observation
 * points every test here asserts on: what was logged, what was toasted, what was reported.
 */
function setup(options: { withMonitor?: boolean; monitor?: ErrorMonitor } = {}) {
  const add = vi.fn<(message: { severity: string; summary: string; detail: string; life: number }) => void>();
  const capture = vi.fn<(error: unknown, report: ErrorReport) => void>();
  const messages = { add } as unknown as MessageService;
  const monitor: ErrorMonitor = options.monitor ?? { captureException: capture };
  const consoleError = vi.spyOn(console, 'error').mockImplementation(() => undefined);

  TestBed.configureTestingModule({
    providers: [
      { provide: MessageService, useValue: messages },
      ...(options.withMonitor === false ? [] : [{ provide: ERROR_MONITOR, useValue: monitor }]),
      GlobalErrorHandler,
    ],
  });

  return { handler: TestBed.inject(GlobalErrorHandler), messages, monitor, consoleError, add, capture };
}

/**
 * Flattens everything written to `console.error` into searchable text. `JSON.stringify` alone is
 * useless here: an `Error` serialises to `{}`, so an assertion built on it silently passes
 * against nothing — which is how a test claiming "it logged the error" ends up asserting the
 * opposite of what it says.
 */
function loggedText(consoleError: ReturnType<typeof setup>['consoleError']): string {
  return consoleError.mock.calls
    .flat()
    .map((arg: unknown) => {
      if (arg instanceof Error) return `${arg.name}: ${arg.message}`;
      if (typeof arg === 'string') return arg;
      return JSON.stringify(arg);
    })
    .join('\n');
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('errorSignature', () => {
  it('gives two throws of the same error from the same place one signature', () => {
    const a = new Error('boom');
    a.stack = 'Error: boom\n    at poll (app.js:10:5)\n    at tick (app.js:20:1)';
    const b = new Error('boom');
    b.stack = 'Error: boom\n    at poll (app.js:10:5)\n    at tick (app.js:99:9)';

    // Same name, same message, same originating frame — the deeper frames differing (an async
    // hop, a different caller) must not split one recurring bug into two.
    expect(errorSignature(a)).toBe(errorSignature(b));
  });

  it('separates two different errors that share generic copy', () => {
    const a = new Error('Network error');
    a.stack = 'Error: Network error\n    at loadItems (items.js:3:1)';
    const b = new Error('Network error');
    b.stack = 'Error: Network error\n    at saveProfile (profile.js:8:1)';

    expect(errorSignature(a)).not.toBe(errorSignature(b));
  });

  it('handles a thrown non-Error without crashing', () => {
    expect(errorSignature('just a string')).toBe('non-error:just a string');
    expect(errorSignature(null)).toBe('non-error:null');
  });
});

describe('describeError', () => {
  it('pulls status, code and traceId off an ApiError so a report can be joined to a server request', () => {
    const error = new ApiError(500, 'Server exploded', null, 'INTERNAL', 'trace-abc-123');

    const report = describeError(error, 3, 'https://app.example.com/items/7');

    expect(report).toMatchObject({
      occurrence: 3,
      name: 'ApiError',
      message: 'Server exploded',
      url: 'https://app.example.com/items/7',
      status: 500,
      code: 'INTERNAL',
      traceId: 'trace-abc-123',
    });
  });

  it('describes a thrown non-Error rather than producing an empty report', () => {
    const report = describeError({ weird: true }, 1, null);

    expect(report.name).toBe('object');
    expect(report.message).toBe('[object Object]');
    expect(report.status).toBeNull();
  });
});

describe('GlobalErrorHandler — it does not swallow', () => {
  it('logs every error to the console, including the ones whose toast is suppressed', () => {
    const { handler, consoleError, add } = setup();
    const error = new Error('flaky poll');
    error.stack = 'Error: flaky poll\n    at poll (app.js:1:1)';

    handler.handleError(error);
    handler.handleError(error);
    handler.handleError(error);

    // One toast (deduped) but three log lines: suppression is a UI decision, never a
    // record-keeping one. This is the regression guard for "silent for a week".
    expect(add).toHaveBeenCalledOnce();
    expect(consoleError).toHaveBeenCalledTimes(3);
  });

  it('logs and toasts even with no monitoring service wired — it degrades to console, never to silence', () => {
    const { handler, consoleError, add } = setup({ withMonitor: false });

    handler.handleError(new Error('boom'));

    expect(consoleError).toHaveBeenCalledOnce();
    expect(add).toHaveBeenCalledOnce();
  });

  it('still logs the original error when the monitoring adapter itself throws', () => {
    const exploding: ErrorMonitor = {
      captureException: () => {
        throw new Error('monitor is down');
      },
    };
    const { handler, consoleError, add } = setup({ monitor: exploding });

    // Must not propagate: an ErrorHandler that throws takes the app down with it.
    expect(() => handler.handleError(new Error('the real problem'))).not.toThrow();

    const logged = loggedText(consoleError);
    expect(logged).toContain('the real problem');
    expect(logged).toContain('monitor is down');
    // The user is STILL told, despite the monitor being broken. One reporting channel failing
    // must never silence the other — the first draft of this handler got that wrong, and this
    // assertion is what caught it.
    expect(add).toHaveBeenCalledOnce();
  });

  it('reports an error that is never toasted, so a suppressed toast is never a lost signal', () => {
    const { handler, add, capture } = setup();
    const error = new ApiError(401, 'Unauthorized', null, null);
    markSessionExpired(error);

    handler.handleError(error);

    // No toast — the login page's own "session expired" message covers it...
    expect(add).not.toHaveBeenCalled();
    // ...but it is emphatically still reported.
    expect(capture).toHaveBeenCalledOnce();
  });
});

describe('GlobalErrorHandler — it reports', () => {
  it('hands the monitor the raw error plus a structured report', () => {
    const { handler, capture } = setup();
    const error = new ApiError(502, 'Bad gateway', null, 'UPSTREAM', 'trace-9');

    handler.handleError(error);

    expect(capture).toHaveBeenCalledOnce();
    const [reported, report] = capture.mock.calls[0];
    expect(reported).toBe(error);
    expect(report).toMatchObject({ status: 502, code: 'UPSTREAM', traceId: 'trace-9', occurrence: 1 });
  });

  it('reports EVERY occurrence with a rising count, even while the toast is deduped', () => {
    const { handler, add, capture } = setup();
    const error = new Error('one broken poll');
    error.stack = 'Error: one broken poll\n    at poll (app.js:1:1)';

    for (let i = 0; i < 50; i++) {
      handler.handleError(error);
    }

    // The whole point of not deduping the monitor: the service whose job is to see the storm
    // sees all fifty, and sees that they are one storm rather than fifty incidents.
    expect(capture).toHaveBeenCalledTimes(50);
    expect(add).toHaveBeenCalledOnce();
    const lastReport = capture.mock.calls[49][1];
    expect(lastReport.occurrence).toBe(50);
  });

  it('puts the traceId in the user-visible toast so support can trace the request', () => {
    const { handler, add } = setup();

    handler.handleError(new ApiError(500, 'Server exploded', null, null, 'trace-abc-123'));

    expect(add).toHaveBeenCalledWith(expect.objectContaining({ detail: expect.stringContaining('trace-abc-123') }));
  });

  it('never puts a stack trace in the toast', () => {
    const { handler, add } = setup();
    const error = new Error('TypeError: cannot read properties of undefined');
    error.stack = 'Error\n    at secretInternals (chunk-XYZ.js:1:1)';

    handler.handleError(error);

    const detail = add.mock.calls[0][0].detail;
    expect(detail).not.toContain('secretInternals');
    expect(detail).toBe('Something went wrong. The problem has been reported.');
  });
});

describe('GlobalErrorHandler — deduplication', () => {
  let clock = 1_000_000;

  beforeEach(() => {
    clock = 1_000_000;
    vi.spyOn(Date, 'now').mockImplementation(() => clock);
  });

  it('raises one toast for a storm of fifty identical errors', () => {
    const { handler, add } = setup();
    const error = new Error('poll failed');
    error.stack = 'Error: poll failed\n    at poll (app.js:1:1)';

    for (let i = 0; i < 50; i++) {
      clock += 100; // a failing 10Hz poll — the whole storm inside one window
      handler.handleError(error);
    }

    expect(add).toHaveBeenCalledOnce();
  });

  it('toasts again once the window has elapsed, so a recurring failure is not muted forever', () => {
    const { handler, add } = setup();
    const error = new Error('poll failed');
    error.stack = 'Error: poll failed\n    at poll (app.js:1:1)';

    handler.handleError(error);
    clock += DUPLICATE_ERROR_WINDOW_MS - 1;
    handler.handleError(error);
    expect(add).toHaveBeenCalledOnce();

    clock += 2;
    handler.handleError(error);
    expect(add).toHaveBeenCalledTimes(2);
  });

  it('does not merge two genuinely different errors into one toast', () => {
    const { handler, add } = setup();
    const a = new Error('items failed');
    a.stack = 'Error: items failed\n    at loadItems (items.js:1:1)';
    const b = new Error('profile failed');
    b.stack = 'Error: profile failed\n    at loadProfile (profile.js:1:1)';

    handler.handleError(a);
    handler.handleError(b);

    expect(add).toHaveBeenCalledTimes(2);
  });

  it('bounds the signature map, so unique-message errors cannot leak memory forever', () => {
    const { handler } = setup();

    for (let i = 0; i < 500; i++) {
      const error = new Error(`failed loading record ${i}`);
      error.stack = `Error\n    at load (app.js:${i}:1)`;
      handler.handleError(error);
    }

    const tracked = (handler as unknown as { seen: Map<string, unknown> }).seen;
    expect(tracked.size).toBeLessThanOrEqual(100);
  });
});

describe('provideGlobalErrorHandler', () => {
  it('installs GlobalErrorHandler as the application ErrorHandler', () => {
    TestBed.configureTestingModule({
      providers: [{ provide: MessageService, useValue: messageServiceStub() }, provideGlobalErrorHandler()],
    });

    expect(TestBed.inject(ErrorHandler)).toBeInstanceOf(GlobalErrorHandler);
  });
});

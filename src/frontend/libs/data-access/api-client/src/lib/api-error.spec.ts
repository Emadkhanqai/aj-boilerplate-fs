import { describe, expect, it } from 'vitest';
import { ApiError, conflictData } from './api-error';

describe('conflictData', () => {
  it('extracts the typed payload from a 409 ApiError whose envelope carries data', () => {
    const envelope = {
      success: false,
      data: { conflictingUser: 'someone.else@example.com', changedAt: '2026-01-01T00:00:00Z' },
      message: 'The request could not be completed.',
      errors: null,
      code: 'REQUEST_FAILED',
      timestamp: '2026-07-28T00:00:00Z',
      traceId: 't-1',
    };
    const err = new ApiError(409, envelope.message, envelope, envelope.code);

    const result = conflictData<{ conflictingUser: string; changedAt: string }>(err);

    expect(result).toEqual({ conflictingUser: 'someone.else@example.com', changedAt: '2026-01-01T00:00:00Z' });
  });

  it('returns undefined for a 409 ApiError whose envelope has no data (e.g. a stale-write conflict)', () => {
    const envelope = { success: false, data: null, message: 'Conflict.', errors: null, code: 'CONFLICT', timestamp: 't', traceId: null };
    const err = new ApiError(409, 'Conflict.', envelope, 'CONFLICT');

    expect(conflictData(err)).toBeUndefined();
  });

  it('returns undefined for a non-409 ApiError', () => {
    const err = new ApiError(404, 'Not found.', { success: false, data: null }, 'NOT_FOUND');

    expect(conflictData(err)).toBeUndefined();
  });

  it('returns undefined for a non-ApiError value', () => {
    expect(conflictData(new Error('network down'))).toBeUndefined();
    expect(conflictData(null)).toBeUndefined();
    expect(conflictData(undefined)).toBeUndefined();
  });
});

import { describe, expect, it } from 'vitest';
import { ApiError } from '@aj-boilerplate/data-access/api-client';
import { adminErrorMessage, rangeToInstants, toSortBy } from './admin-support';

describe('adminErrorMessage', () => {
  it.each([
    [422, 'email domain is not allowed'],
    [404, 'no seat'],
    [400, 'not valid'],
    [502, 'not answering'],
    [503, 'not answering'],
  ])('explains %s', (status, text) => {
    expect(adminErrorMessage(new ApiError(status, 'x', null), 'fallback')).toContain(text);
  });
  it('uses the caller fallback for anything else', () => {
    expect(adminErrorMessage(new ApiError(500, 'x', null), 'fallback')).toBe('fallback');
    expect(adminErrorMessage(new Error('boom'), 'fallback')).toBe('fallback');
  });
});

describe('rangeToInstants', () => {
  it('sends the end day EXCLUSIVE: 5 Nov to 6 Nov ends at the start of 7 Nov', () => {
    expect(rangeToInstants([new Date(2026, 10, 5), new Date(2026, 10, 6)])).toEqual({
      from: '2026-11-05T00:00:00.000Z',
      to: '2026-11-07T00:00:00.000Z',
    });
  });
  it('treats a single picked day as that whole day', () => {
    expect(rangeToInstants([new Date(2026, 10, 5), null])).toEqual({
      from: '2026-11-05T00:00:00.000Z',
      to: '2026-11-06T00:00:00.000Z',
    });
  });
  it('sends nothing when no range is picked', () => {
    expect(rangeToInstants(null)).toEqual({});
  });
});

describe('toSortBy', () => {
  it('passes the API vocabulary and defaults an unknown column to signedIn', () => {
    expect(toSortBy('duration')).toBe('duration');
    expect(toSortBy('nonsense')).toBe('signedIn');
    expect(toSortBy(undefined)).toBe('signedIn');
  });
});

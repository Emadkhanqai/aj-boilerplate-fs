import { describe, expect, it } from 'vitest';
import { validatePositiveInt } from './validate-positive-int';

describe('validatePositiveInt', () => {
  it('accepts a positive whole number', () => {
    expect(validatePositiveInt('42')).toEqual({ value: 42, error: null });
  });

  it('trims surrounding whitespace', () => {
    expect(validatePositiveInt('  7 ')).toEqual({ value: 7, error: null });
  });

  it.each([
    ['', 'Required.'],
    ['abc', 'Whole number only.'],
    ['1.5', 'Whole number only.'],
    ['-3', 'Whole number only.'],
    ['0', 'Must be 1 or more.'],
  ])('rejects %j with %j', (raw, error) => {
    expect(validatePositiveInt(raw)).toEqual({ value: null, error });
  });

  it('rejects a value above MAX_SAFE_INTEGER, which would silently lose precision', () => {
    expect(validatePositiveInt('9007199254740993')).toEqual({
      value: null,
      error: 'Number is too large.',
    });
  });

  it('accepts MAX_SAFE_INTEGER itself', () => {
    expect(validatePositiveInt(String(Number.MAX_SAFE_INTEGER)).value).toBe(Number.MAX_SAFE_INTEGER);
  });
});

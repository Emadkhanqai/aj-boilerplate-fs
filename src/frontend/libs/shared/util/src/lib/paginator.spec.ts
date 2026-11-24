import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DEFAULT_PAGE_SIZE, PAGE_SIZE_OPTIONS, rememberPageSize, rememberedPageSize } from './paginator';
import { formatDuration } from './format';

describe('paginator preference', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => vi.restoreAllMocks());

  it('is the standard: 10/20/50/100, default 20', () => {
    expect(PAGE_SIZE_OPTIONS).toEqual([10, 20, 50, 100]);
    expect(rememberedPageSize('t')).toBe(DEFAULT_PAGE_SIZE);
  });
  it('remembers a choice per table', () => {
    rememberPageSize('a', 50);
    expect(rememberedPageSize('a')).toBe(50);
    expect(rememberedPageSize('b')).toBe(20);
  });
  it('ignores a stored value that is not one of the options', () => {
    localStorage.setItem('aj.pageSize.a', '7');
    expect(rememberedPageSize('a')).toBe(20);
  });
  it('falls back to 20 and does not throw when storage is blocked', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    expect(rememberedPageSize('a')).toBe(20);
    expect(() => rememberPageSize('a', 50)).not.toThrow();
  });
});

describe('formatDuration', () => {
  it.each([
    [45, '45s'],
    [725, '12m 05s'],
    [11520, '3h 12m'],
    [null, '—'],
    [-1, '—'],
  ])('%s -> %s', (input, out) => expect(formatDuration(input)).toBe(out));
});

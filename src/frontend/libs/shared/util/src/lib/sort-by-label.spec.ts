import { describe, expect, it } from 'vitest';
import { sortByLabel } from './sort-by-label';

describe('sortByLabel', () => {
  it('sorts A-Z by label (default)', () => {
    const result = sortByLabel(['banana', 'Apple', 'cherry'], (s) => s);
    expect(result).toEqual(['Apple', 'banana', 'cherry']);
  });

  it('preserves input order when alpha: false', () => {
    const result = sortByLabel(['banana', 'Apple', 'cherry'], (s) => s, { alpha: false });
    expect(result).toEqual(['banana', 'Apple', 'cherry']);
  });

  it('sorts numeric prefixes naturally (2 before 10)', () => {
    const result = sortByLabel(['10 · Ten', '2 · Two'], (s) => s);
    expect(result).toEqual(['2 · Two', '10 · Ten']);
  });
});

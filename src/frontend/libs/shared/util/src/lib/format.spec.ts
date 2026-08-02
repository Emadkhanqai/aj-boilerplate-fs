import { describe, expect, it } from 'vitest';
import { formatBytes, formatDate, formatDateTime, humanizePascalCase, initialsOf } from './format';

describe('formatDate', () => {
  it('formats an ISO instant', () => {
    expect(formatDate('2026-07-07T14:30:00Z')).toBe('07 Jul 2026');
  });

  it('anchors a zoneless timestamp to UTC rather than the viewer local time', () => {
    expect(formatDate('2026-07-07T23:30:00')).toBe('07 Jul 2026');
  });

  it('returns an em dash for nullish/empty input', () => {
    expect(formatDate(null)).toBe('—');
    expect(formatDate(undefined)).toBe('—');
    expect(formatDate('')).toBe('—');
  });

  it('returns the raw value back when it cannot be parsed', () => {
    expect(formatDate('not-a-date')).toBe('not-a-date');
  });
});

describe('formatDateTime', () => {
  it('formats date and time', () => {
    expect(formatDateTime('2026-07-07T14:30:00Z')).toBe('07 Jul 2026, 14:30');
  });

  it('renders a zoneless timestamp at the same wall clock (UTC display zone)', () => {
    expect(formatDateTime('2026-07-07T08:09:00')).toBe('07 Jul 2026, 08:09');
  });
});

describe('formatBytes', () => {
  it.each([
    [0, '0 B'],
    [512, '512 B'],
    [1024, '1.0 KB'],
    [1024 * 1024 * 3.5, '3.5 MB'],
    [1024 * 1024 * 1024 * 2, '2.0 GB'],
  ])('formats %i as %s', (bytes, expected) => {
    expect(formatBytes(bytes)).toBe(expected);
  });

  it('returns an em dash for nullish or negative input', () => {
    expect(formatBytes(null)).toBe('—');
    expect(formatBytes(-1)).toBe('—');
  });
});

describe('humanizePascalCase', () => {
  it('splits PascalCase into words', () => {
    expect(humanizePascalCase('ArchivedByOwner')).toBe('Archived By Owner');
  });

  it('leaves a single word alone', () => {
    expect(humanizePascalCase('Draft')).toBe('Draft');
  });
});

describe('initialsOf', () => {
  it('takes the first letter of the first two name parts', () => {
    expect(initialsOf('Avery Admin')).toBe('AA');
  });

  it('works from an email address', () => {
    expect(initialsOf('evan.editor@example.com')).toBe('EE');
  });

  it('falls back to a question mark for an empty value', () => {
    expect(initialsOf('')).toBe('?');
  });
});

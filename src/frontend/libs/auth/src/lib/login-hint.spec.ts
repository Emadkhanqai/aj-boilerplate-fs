import { describe, expect, it } from 'vitest';
import { isWellFormedEmail, loginHintFor } from './login-hint';

describe('loginHintFor', () => {
  it('returns the trimmed address when it is well formed', () => {
    expect(loginHintFor('  a.b@example.com ')).toBe('a.b@example.com');
  });

  it.each(['', '   ', 'a@b', 'no-at-sign', 'two@@example.com', 'a b@example.com'])(
    'returns null for %j, so a bad value costs the hint and never the sign-in',
    (raw) => {
      expect(loginHintFor(raw)).toBeNull();
    },
  );

  it('returns null when there is no value at all', () => {
    expect(loginHintFor(undefined)).toBeNull();
  });

  it('shares one definition of "well formed" with the page that gates on it', () => {
    expect(isWellFormedEmail('a@b.co')).toBe(true);
    expect(isWellFormedEmail('a@b')).toBe(false);
  });
});

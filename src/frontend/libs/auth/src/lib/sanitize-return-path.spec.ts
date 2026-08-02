import { describe, expect, it } from 'vitest';
import { sanitizeReturnPath } from './sanitize-return-path';

describe('sanitizeReturnPath', () => {
  it('rejects an absolute external URL, falling back to "/"', () => {
    expect(sanitizeReturnPath('https://evil.example/phish')).toBe('/');
  });

  it('rejects a protocol-relative URL, falling back to "/"', () => {
    expect(sanitizeReturnPath('//evil.example/phish')).toBe('/');
  });

  it('accepts a normal same-origin path unchanged', () => {
    expect(sanitizeReturnPath('/items/42')).toBe('/items/42');
  });

  it('falls back to "/" for an empty string', () => {
    expect(sanitizeReturnPath('')).toBe('/');
  });

  it('preserves query and hash for a legitimate same-origin path', () => {
    expect(sanitizeReturnPath('/items/x?tab=1#top')).toBe('/items/x?tab=1#top');
  });

  it('rejects a backslash-authority bypass (WHATWG URL treats `\\` as `/` in authority position), falling back to "/"', () => {
    expect(sanitizeReturnPath('/\\evil.example/phish')).toBe('/');
  });

  it('rejects a tab-smuggled authority bypass (the URL parser strips tabs before parsing), falling back to "/"', () => {
    expect(sanitizeReturnPath('/\t/evil.example')).toBe('/');
  });

  it('rejects a newline-smuggled authority bypass (the URL parser strips LF before parsing), falling back to "/"', () => {
    expect(sanitizeReturnPath('/\n/evil.example')).toBe('/');
  });

  it('rejects a carriage-return-smuggled authority bypass (the URL parser strips CR before parsing), falling back to "/"', () => {
    expect(sanitizeReturnPath('/\r/evil.example')).toBe('/');
  });

  describe('auth-protocol parameter stripping', () => {
    it('strips an echoed-back `state` from a bare post-logout landing, yielding "/"', () => {
      expect(sanitizeReturnPath('/?state=abc')).toBe('/');
    });

    it('strips a base64 `state` blob an identity provider echoes back after logout', () => {
      const state = 'eyJpZCI6IjAxOWY5ZmQ5LThmODYtNzYwOS1iMzFjLTk3MDE0MTZkZjVmYSIsIm1ldGEiOnsiaW50ZXJhY3Rpb25UeXBlIjoicmVkaXJlY3QifX0%3D';
      expect(sanitizeReturnPath(`/?state=${state}`)).toBe('/');
    });

    it('strips `state` while keeping legitimate application query params', () => {
      expect(sanitizeReturnPath('/items?state=x&foo=1')).toBe('/items?foo=1');
    });

    it.each([
      'code',
      'session_state',
      'error',
      'error_description',
      'error_uri',
      'client_info',
      'id_token',
    ])('strips the `%s` protocol param', (param) => {
      expect(sanitizeReturnPath(`/items?${param}=zzz&tab=1`)).toBe('/items?tab=1');
    });

    it('strips every protocol param at once, including repeated ones', () => {
      expect(sanitizeReturnPath('/settings?state=a&state=b&code=c&error=d&keep=yes')).toBe('/settings?keep=yes');
    });

    it('keeps the hash intact while stripping protocol params', () => {
      expect(sanitizeReturnPath('/items/x?state=a&tab=1#top')).toBe('/items/x?tab=1#top');
    });

    it('does not strip params that merely resemble protocol params', () => {
      expect(sanitizeReturnPath('/items?stateId=7&codes=abc')).toBe('/items?stateId=7&codes=abc');
    });

    it('still rejects an off-origin URL even when it carries no protocol params', () => {
      expect(sanitizeReturnPath('https://evil.example/phish?state=a')).toBe('/');
    });
  });
});

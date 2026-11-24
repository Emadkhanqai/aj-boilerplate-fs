import { afterEach, describe, expect, it } from 'vitest';
import { resolveAuthMode } from './provider-factory';

describe('resolveAuthMode', () => {
  const g = globalThis as { __APP_AUTH_MODE__?: string };
  afterEach(() => delete g.__APP_AUTH_MODE__);

  it('is dev only when env.js says exactly dev', () => {
    g.__APP_AUTH_MODE__ = 'dev';
    expect(resolveAuthMode()).toBe('dev');
  });

  it.each(['gateway', '', 'oidc', 'DEV', 'dev '])('fails CLOSED to gateway for %j', (value) => {
    g.__APP_AUTH_MODE__ = value;
    expect(resolveAuthMode()).toBe('gateway');
  });

  it('fails closed to gateway when env.js never ran', () => {
    expect(resolveAuthMode()).toBe('gateway');
  });
});

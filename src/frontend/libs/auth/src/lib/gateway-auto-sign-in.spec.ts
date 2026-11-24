import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { GATEWAY_AUTO_SIGN_IN_WINDOW_MS, claimGatewayAutoSignIn } from './gateway-auto-sign-in';

describe('claimGatewayAutoSignIn', () => {
  beforeEach(() => window.sessionStorage.clear());
  afterEach(() => vi.restoreAllMocks());

  it('grants the first claim and refuses the immediate second: the redirect-loop brake', () => {
    expect(claimGatewayAutoSignIn(1_000)).toBe(true);
    expect(claimGatewayAutoSignIn(1_001)).toBe(false);
  });

  it('grants again once the window has passed, and not a millisecond before', () => {
    claimGatewayAutoSignIn(1_000);

    expect(claimGatewayAutoSignIn(1_000 + GATEWAY_AUTO_SIGN_IN_WINDOW_MS - 1)).toBe(false);
    expect(claimGatewayAutoSignIn(1_000 + GATEWAY_AUTO_SIGN_IN_WINDOW_MS)).toBe(true);
  });

  it('treats an unreadable stamp as a LIVE claim (ask for a click, never loop)', () => {
    window.sessionStorage.setItem('app.auth.gateway-auto-sign-in', 'garbage');

    expect(claimGatewayAutoSignIn(1_000)).toBe(false);
  });

  it('fails safe when storage throws on read', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });

    expect(claimGatewayAutoSignIn(1_000)).toBe(false);
  });

  it('fails safe when the claim cannot be recorded', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('quota');
    });

    expect(claimGatewayAutoSignIn(1_000)).toBe(false);
  });
});

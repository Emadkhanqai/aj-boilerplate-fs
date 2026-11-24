import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { consumeSignedOut, markSignedOut } from './gateway-signed-out-marker';

describe('signed-out marker', () => {
  beforeEach(() => window.localStorage.clear());
  afterEach(() => vi.restoreAllMocks());

  it('reports nothing before any sign-out (positive control: the empty answer is false)', () => {
    expect(consumeSignedOut()).toBe(false);
  });

  it('is consumed by the read: one challenge per sign-out, not one per arrival', () => {
    markSignedOut();

    expect(consumeSignedOut()).toBe(true);
    expect(consumeSignedOut()).toBe(false);
  });

  it('fails toward NO prompt when storage cannot be read', () => {
    markSignedOut();
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });

    expect(consumeSignedOut()).toBe(false);
  });

  it('never throws out of a sign-out when storage cannot be written', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('quota');
    });

    expect(() => markSignedOut()).not.toThrow();
  });
});

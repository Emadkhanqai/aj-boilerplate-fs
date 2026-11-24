import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { consumeSignedOut } from '../gateway-signed-out-marker';
import type { AuthProviderApi } from '../auth.types';
import { createGatewayProvider, gatewayNavigation } from './gateway-provider';

describe('gateway provider', () => {
  let provider: AuthProviderApi;
  let http: HttpTestingController;
  let navigate: ReturnType<typeof vi.fn<(url: string) => void>>;

  function at(search: string): void {
    window.history.replaceState({}, '', `/login${search}`);
  }

  beforeEach(() => {
    window.localStorage.clear();
    navigate = vi.fn<(url: string) => void>();
    vi.spyOn(gatewayNavigation, 'navigate').mockImplementation(navigate);
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    provider = TestBed.runInInjectionContext(() => createGatewayProvider());
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    vi.restoreAllMocks();
    window.history.replaceState({}, '', '/');
  });

  it('holds no token and grants nothing until /api/v1/me answers', () => {
    const session = provider.restore();

    expect(provider.mode).toBe('gateway');
    expect(provider.ambientCredential).toBe(true);
    expect(provider.devUsers).toBeNull();
    expect(session).toEqual({ user: { userId: '', name: '', email: '' }, roles: [] });
    expect(Object.keys(session ?? {})).not.toContain('accessToken');
  });

  describe('signIn', () => {
    it('does a FULL-PAGE navigation to /auth/login carrying the sanitized return path as rd', () => {
      at('?from=%2Fitems%3Ftab%3D1');

      void provider.signIn();

      expect(navigate).toHaveBeenCalledTimes(1);
      expect(navigate).toHaveBeenCalledWith('/auth/login?rd=%2Fitems%3Ftab%3D1');
    });

    it('falls back to "/" when there is no from', () => {
      at('');

      void provider.signIn();

      expect(navigate).toHaveBeenCalledWith('/auth/login?rd=%2F');
    });

    it('refuses an off-origin from: the open-redirect guard runs before the hop', () => {
      at('?from=https%3A%2F%2Fevil.example%2Fphish');

      void provider.signIn();

      expect(navigate).toHaveBeenCalledWith('/auth/login?rd=%2F');
    });

    it('appends a well-formed login_hint, encoded, and drops a malformed one', () => {
      at('');

      void provider.signIn(undefined, 'a+b@example.com');
      void provider.signIn(undefined, 'not-an-email');

      expect(navigate).toHaveBeenNthCalledWith(1, '/auth/login?rd=%2F&login_hint=a%2Bb%40example.com');
      expect(navigate).toHaveBeenNthCalledWith(2, '/auth/login?rd=%2F');
    });

    it('never settles: the browser is leaving, so the page must not run its own navigation', async () => {
      at('');
      let settled = false;

      void provider.signIn().then(
        () => (settled = true),
        () => (settled = true),
      );
      await new Promise((resolve) => setTimeout(resolve, 20));

      expect(settled).toBe(false);
    });
  });

  describe('signOut', () => {
    it('POSTs /auth/logout, then goes to /logged-out', async () => {
      const done = provider.signOut();

      const req = http.expectOne('/auth/logout');
      expect(req.request.method).toBe('POST');
      expect(navigate).not.toHaveBeenCalled();
      req.flush(null);
      await done;

      expect(navigate).toHaveBeenCalledWith('/logged-out');
    });

    it('still leaves for /logged-out when the POST fails', async () => {
      const done = provider.signOut();

      http.expectOne('/auth/logout').flush(null, { status: 503, statusText: 'Service Unavailable' });
      await Promise.resolve(done).catch(() => undefined);

      expect(navigate).toHaveBeenCalledWith('/logged-out');
    });

    it('marks the browser so the NEXT sign-in sends prompt=login, once', async () => {
      const done = provider.signOut();
      http.expectOne('/auth/logout').flush(null);
      await done;
      at('');

      void provider.signIn();
      void provider.signIn();

      expect(navigate).toHaveBeenNthCalledWith(2, '/auth/login?rd=%2F&prompt=login');
      expect(navigate).toHaveBeenNthCalledWith(3, '/auth/login?rd=%2F');
    });

    it('marks the browser BEFORE the POST resolves, so a hung request still counts as signed out', () => {
      void provider.signOut();

      expect(consumeSignedOut()).toBe(true);
      http.expectOne('/auth/logout').flush(null);
    });
  });

  it('reads the profile from /api/v1/me', async () => {
    const profile = { userId: 'u1', displayName: 'U', email: 'u@example.com', roles: ['Viewer'], capabilities: {} };
    const result = provider.fetchProfile({ user: { userId: '', name: '', email: '' }, roles: [] });

    http.expectOne('/api/v1/me').flush(profile);

    await expect(result).resolves.toEqual(profile);
  });
});

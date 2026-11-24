import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { fireEvent, render, screen } from '@testing-library/angular';
import { providePrimeNG } from 'primeng/config';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthService, GATEWAY_AUTO_SIGN_IN_WINDOW_MS } from '@aj-boilerplate/auth';
import { LoginPageComponent } from './login-page';

interface AuthStub {
  mode: 'gateway' | 'dev';
  devUsers: null;
  sessionExpired: () => boolean;
  signIn: ReturnType<typeof vi.fn>;
}

async function renderLogin(opts: { from?: string; mode?: 'gateway' | 'dev'; expired?: boolean; failSignIn?: boolean } = {}) {
  const auth: AuthStub = {
    mode: opts.mode ?? 'gateway',
    devUsers: null,
    sessionExpired: signal(opts.expired ?? false),
    // A real gateway sign-in never settles (the browser leaves); a failing one lets the button be pressed again.
    signIn: vi.fn(() => (opts.failSignIn === true ? Promise.reject(new Error('x')) : new Promise<void>(() => undefined))),
  };
  const query = opts.from === undefined ? {} : { from: opts.from };
  const view = await render(LoginPageComponent, {
    providers: [
      provideZonelessChangeDetection(),
      providePrimeNG({}),
      provideRouter([]),
      { provide: AuthService, useValue: auth },
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } },
    ],
  });
  return { auth, view };
}

describe('LoginPageComponent (gateway)', () => {
  beforeEach(() => window.sessionStorage.clear());
  afterEach(() => window.sessionStorage.clear());

  it('starts the sign-in by itself when the gateway sent the visitor here (from is present)', async () => {
    const { auth } = await renderLogin({ from: '/items' });

    expect(auth.signIn).toHaveBeenCalledTimes(1);
  });

  it('does NOT auto-start without a from: a visitor who typed /login presses the button', async () => {
    const { auth } = await renderLogin();

    expect(auth.signIn).not.toHaveBeenCalled();
  });

  it('holds the SECOND automatic attempt inside the window, and says so: the loop brake', async () => {
    await renderLogin({ from: '/items' });
    TestBed.resetTestingModule(); // a fresh page load: same tab, so sessionStorage survives
    const second = await renderLogin({ from: '/items' });

    expect(second.auth.signIn).not.toHaveBeenCalled();
    expect(await screen.findByTestId('auto-sign-in-held')).toBeTruthy();
  });

  it('control: the brake releases after the window', async () => {
    vi.spyOn(Date, 'now').mockReturnValue(1_000);
    await renderLogin({ from: '/items' });
    TestBed.resetTestingModule();
    vi.spyOn(Date, 'now').mockReturnValue(1_000 + GATEWAY_AUTO_SIGN_IN_WINDOW_MS);
    const later = await renderLogin({ from: '/items' });

    expect(later.auth.signIn).toHaveBeenCalledTimes(1);
    vi.restoreAllMocks();
  });

  it('does not auto-start right after a session expired: the notice is read first', async () => {
    const { auth } = await renderLogin({ from: '/items', expired: true });

    expect(auth.signIn).not.toHaveBeenCalled();
    expect(screen.getByText(/session has expired/i)).toBeTruthy();
  });

  it('passes a well-formed email as the login hint and drops a malformed one', async () => {
    const { auth } = await renderLogin({ failSignIn: true });
    const email = screen.getByTestId('login-email') as HTMLInputElement;

    fireEvent.input(email, { target: { value: 'ada@example.com' } });
    fireEvent.click(screen.getByRole('button', { name: /sign in with single sign-on/i }));
    expect(auth.signIn).toHaveBeenLastCalledWith(undefined, 'ada@example.com');

    fireEvent.input(email, { target: { value: 'not-an-email' } });
    fireEvent.click(screen.getByRole('button', { name: /sign in with single sign-on/i }));
    expect(auth.signIn).toHaveBeenLastCalledWith(undefined, undefined);
  });

  it('never auto-starts in dev mode: the role picker is the sign-in', async () => {
    const { auth } = await renderLogin({ from: '/items', mode: 'dev' });

    expect(auth.signIn).not.toHaveBeenCalled();
  });
});

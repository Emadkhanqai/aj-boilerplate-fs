import { TestBed } from '@angular/core/testing';
import { Router, UrlTree, provideRouter } from '@angular/router';
import type { ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { AuthService } from './auth.service';
import { authGuard } from './auth.guard';

function run(authenticated: boolean, url: string): { result: boolean | UrlTree; router: Router } {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), { provide: AuthService, useValue: { isAuthenticated: () => authenticated } }],
  });
  const router = TestBed.inject(Router);
  const result = TestBed.runInInjectionContext(
    () => authGuard({} as ActivatedRouteSnapshot, { url } as RouterStateSnapshot) as boolean | UrlTree,
  );
  return { result, router };
}

describe('authGuard', () => {
  it('lets an authenticated session through', () => {
    expect(run(true, '/items').result).toBe(true);
  });

  it('sends a visitor with no session to /login, remembering where they were going', () => {
    const { result, router } = run(false, '/items/5?tab=2');

    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/login?from=%2Fitems%2F5%3Ftab%3D2');
  });

  it('sends the bare root to a clean /login rather than ?from=%2F', () => {
    const { result, router } = run(false, '/');

    expect(router.serializeUrl(result as UrlTree)).toBe('/login');
  });

  it('strips identity-provider protocol params from the remembered target', () => {
    const { result, router } = run(false, '/items?state=abc&tab=1');

    expect(router.serializeUrl(result as UrlTree)).toBe('/login?from=%2Fitems%3Ftab%3D1');
  });
});

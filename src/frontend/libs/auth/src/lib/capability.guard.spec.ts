import { signal } from '@angular/core';
import type { Signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, UrlTree, provideRouter } from '@angular/router';
import type { ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';
import { isObservable } from 'rxjs';
import type { Observable } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { AuthService } from './auth.service';
import { capabilityGuard } from './capability.guard';
import { NO_CAPABILITIES, capabilitiesFor } from './roles';
import type { Capabilities, Capability } from './roles';

interface Stub {
  authenticated?: boolean;
  unavailable?: boolean;
  loading?: Signal<boolean>;
  capabilities?: Capabilities;
}

function guard(capability: Capability, stub: Stub, url = '/items/new') {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      {
        provide: AuthService,
        useValue: {
          isAuthenticated: () => stub.authenticated ?? true,
          capabilitiesUnavailable: () => stub.unavailable ?? false,
          capabilitiesLoading: stub.loading ?? signal(false),
          capabilities: () => stub.capabilities ?? NO_CAPABILITIES,
        },
      },
    ],
  });
  const router = TestBed.inject(Router);
  const result = TestBed.runInInjectionContext(
    () => capabilityGuard(capability)({} as ActivatedRouteSnapshot, { url } as RouterStateSnapshot),
  );
  return { result, router };
}

const asUrl = (r: { result: unknown; router: Router }): string => r.router.serializeUrl(r.result as UrlTree);

describe('capabilityGuard', () => {
  it('allows a user who holds the capability', () => {
    expect(guard('canCreate', { capabilities: capabilitiesFor('editor') }).result).toBe(true);
  });

  it('sends a user who lacks it to a VISIBLE access-denied page naming the capability', () => {
    const r = guard('canDelete', { capabilities: capabilitiesFor('editor') });

    expect(asUrl(r)).toBe('/access-denied?capability=canDelete');
  });

  it('says "could not check", not "denied", when the profile could not be read (an outage is not a verdict)', () => {
    const r = guard('canCreate', { unavailable: true, capabilities: NO_CAPABILITIES }, '/items/new');

    expect(asUrl(r)).toBe('/access-check-unavailable?from=%2Fitems%2Fnew');
  });

  it('sends a visitor with no session to /login', () => {
    const r = guard('canView', { authenticated: false }, '/items');

    expect(asUrl(r)).toBe('/login?from=%2Fitems');
  });

  it('waits for the profile instead of denying every gated route on a cold load', () => {
    const loading = signal(true);
    const caps = signal<Capabilities>(NO_CAPABILITIES);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: AuthService,
          useValue: {
            isAuthenticated: () => true,
            capabilitiesUnavailable: () => false,
            capabilitiesLoading: loading,
            capabilities: () => caps(),
          },
        },
      ],
    });
    const result = TestBed.runInInjectionContext(
      () => capabilityGuard('canEdit')({} as ActivatedRouteSnapshot, { url: '/items/1' } as RouterStateSnapshot),
    );
    expect(isObservable(result)).toBe(true);
    const seen: unknown[] = [];
    (result as Observable<boolean | UrlTree>).subscribe((v) => seen.push(v));

    TestBed.tick();
    expect(seen).toEqual([]);

    caps.set(capabilitiesFor('editor'));
    loading.set(false);
    TestBed.tick();

    expect(seen).toEqual([true]);
  });
});

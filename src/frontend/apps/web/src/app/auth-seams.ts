import { inject } from '@angular/core';
import type { Provider } from '@angular/core';
import { AMBIENT_SESSION_ACTIVE, SESSION_EXPIRED_NOTIFIER } from '@aj-boilerplate/data-access/api-client';
import { AuthService } from '@aj-boilerplate/auth';

/**
 * The DI seams that let `data-access` react to auth without importing `libs/auth` (the
 * module-boundary rule forbids that direction), each bound to the real `AuthService`.
 *
 * One list, used by `app.config.ts` AND by composition specs, so a seam added to the app cannot be
 * forgotten in a spec that then proves the old chain while reading as proof of the new one.
 */
export function provideAuthSeams(): Provider[] {
  return [
    {
      provide: SESSION_EXPIRED_NOTIFIER,
      useFactory: () => {
        const auth = inject(AuthService);
        return () => auth.notifySessionExpired();
      },
    },
    {
      // Lets the interceptor tell a 401 that ENDED a gateway session from one that answered an
      // anonymous visitor on the sign-in page.
      provide: AMBIENT_SESSION_ACTIVE,
      useFactory: () => {
        const auth = inject(AuthService);
        return () => auth.hasAmbientSession();
      },
    },
  ];
}

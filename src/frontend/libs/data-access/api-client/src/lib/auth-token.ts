import { InjectionToken } from '@angular/core';

/**
 * Provides the current bearer token (or null). Bound by `AuthService` (`libs/auth`) in
 * `app.config.ts`, so this library stays decoupled from the auth layer's session storage — the
 * module-boundary rule forbids `scope:data-access` from importing `scope:auth`, and this DI seam
 * is how the dependency is inverted instead.
 */
export const AUTH_TOKEN_PROVIDER = new InjectionToken<() => string | null>('AUTH_TOKEN_PROVIDER');

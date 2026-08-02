import { createDevProvider } from './dev-provider';
import { createOidcProvider } from './oidc-provider';
import type { AuthMode, AuthProviderApi } from '../auth.types';

/**
 * Resolves the RUNTIME auth mode from `window.__APP_AUTH_MODE__` (default `dev`). That global is
 * set by `apps/web/public/env.js`, which the container entrypoint (`docker/40-env.sh`) rewrites
 * per deployment — so one built artifact can be promoted across environments without a rebuild.
 * Exported so the `/auth/callback` route can dispatch to the right completion function without
 * duplicating this lookup.
 */
export function resolveAuthMode(): AuthMode {
  const mode = (globalThis as { __APP_AUTH_MODE__?: string }).__APP_AUTH_MODE__;
  return mode === 'oidc' ? 'oidc' : 'dev';
}

/**
 * Selects the auth provider from the resolved mode (default `dev`). This is the only place the
 * mode is switched, so the rest of the app is provider-agnostic. Must be called from within an
 * Angular injection context (see `oidc-provider.ts`'s use of `inject`).
 */
export function createAuthProvider(): AuthProviderApi {
  return resolveAuthMode() === 'oidc' ? createOidcProvider() : createDevProvider();
}

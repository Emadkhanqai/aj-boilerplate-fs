import { createDevProvider } from './dev-provider';
import { createGatewayProvider } from './gateway-provider';
import type { AuthMode, AuthProviderApi } from '../auth.types';

/**
 * Resolves the RUNTIME auth mode from `window.__APP_AUTH_MODE__`. That global is set by
 * `apps/web/public/env.js`, which the container entrypoint (`docker/40-env.sh`) rewrites per
 * deployment, so one built artifact can be promoted across environments without a rebuild.
 *
 * Default is `gateway`: anything but an explicit `dev` fails CLOSED to the real thing. `dev` — the
 * role picker, no identity provider — must be asked for by name.
 */
export function resolveAuthMode(): AuthMode {
  const mode = (globalThis as { __APP_AUTH_MODE__?: string }).__APP_AUTH_MODE__;
  return mode === 'dev' ? 'dev' : 'gateway';
}

/**
 * Selects the auth provider from the resolved mode. The only place the mode is switched. Must be
 * called inside an Angular injection context (the gateway provider injects `HttpClient`).
 */
export function createAuthProvider(): AuthProviderApi {
  return resolveAuthMode() === 'dev' ? createDevProvider() : createGatewayProvider();
}

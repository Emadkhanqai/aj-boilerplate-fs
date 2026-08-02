import { RenderMode, ServerRoute } from '@angular/ssr';

/**
 * Every route in `appRoutes` is behind `authGuard` (session-dependent, so not a static-content
 * candidate) or is a small public auth screen. Neither is a fit for build-time prerendering, so
 * the whole app renders per-request. Revisit per-route if you add genuinely static content.
 */
export const serverRoutes: ServerRoute[] = [
  {
    path: '**',
    renderMode: RenderMode.Server,
  },
];

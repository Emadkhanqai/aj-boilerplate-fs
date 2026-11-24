import {
  ApplicationConfig,
  Injector,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { PreloadAllModules, provideRouter, withPreloading } from '@angular/router';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { appRoutes } from './app.routes';
import { provideClientHydration, withEventReplay } from '@angular/platform-browser';
import { provideTanStackQuery } from '@tanstack/angular-query-experimental';
import { providePrimeNG } from 'primeng/config';
import { MessageService } from 'primeng/api';
import {
  ACCESS_REVOKED_OBSERVER,
  FORBIDDEN_OBSERVER,
  accessRevokedInterceptor,
  authInterceptor,
  envelopeInterceptor,
} from '@aj-boilerplate/data-access/api-client';
import { QUERY_CLIENT, provideGlobalErrorHandler } from '@aj-boilerplate/shared/ui';
import { provideDocumentDirection } from '@aj-boilerplate/shared/util';
import { AccessRevokedWatcher, RoleChangeWatcher } from '@aj-boilerplate/shell';
import { appPreset } from '../styles/app-preset';
import { provideAuthSeams } from './auth-seams';
import { readCspNonce } from './csp-nonce';

export const appConfig: ApplicationConfig = {
  providers: [
    provideClientHydration(withEventReplay()),
    // Routes window-level `error`/`unhandledrejection` events into Angular's `ErrorHandler`.
    // Pointless on its own — the DEFAULT handler just writes to a console nobody has open. It is
    // only half of the wiring; `provideGlobalErrorHandler()` below is the other half.
    provideBrowserGlobalErrorListeners(),
    // Replaces Angular's default `ErrorHandler`. Every unhandled error is logged with diagnostic
    // context, reported to `ERROR_MONITOR` if one is wired, and surfaced to the user as a
    // deduplicated toast. Nothing fails silently. See `global-error-handler.ts`.
    provideGlobalErrorHandler(),
    // Keeps the document's `dir`/`lang` in step with `LanguageService`, so picking Arabic
    // actually mirrors the layout instead of rendering Arabic text in an LTR shell.
    // See ADR-0010 for what is and is not supported here.
    provideDocumentDirection(),
    // Every `loadComponent` route still splits out of the INITIAL bundle, but once the first
    // render is done the router preloads all of them in the background, so the first actual
    // navigation to any of them is instant instead of paying its chunk's network cost then.
    provideRouter(appRoutes, withPreloading(PreloadAllModules)),
    // Interceptor ORDER matters. `authInterceptor` comes before `envelopeInterceptor` so that it
    // inspects the unwrapped `ApiError`; `accessRevokedInterceptor` comes AFTER it (so it sits
    // closest to the network) because it needs the raw `HttpErrorResponse` and its headers, which
    // the envelope discards. None of them attaches a credential: the cookie rides along and nginx
    // adds the `Authorization` header (docs/auth/gateway-contract.md).
    provideHttpClient(withFetch(), withInterceptors([authInterceptor, envelopeInterceptor, accessRevokedInterceptor])),
    // `MessageService` backs the single `<p-toast>` mounted at the true root (`app.html`) —
    // provided here so it is one app-wide singleton, and so `QUERY_CLIENT`'s factory can inject
    // it to report otherwise-unhandled API failures as toasts.
    MessageService,
    provideTanStackQuery(QUERY_CLIENT),
    providePrimeNG({
      theme: {
        preset: appPreset,
        options: { darkModeSelector: false },
      },
      // Render every overlay (select/autocomplete panels…) on <body>: the design system's
      // `.panel` has `overflow: hidden` and dialogs stack, which otherwise clips them.
      overlayAppendTo: 'body',
      // Lets PrimeNG's runtime-injected <style> tags through the gateway's nonce-based CSP.
      csp: { nonce: readCspNonce() },
    }),
    ...provideAuthSeams(),
    {
      // Resolved lazily through the injector: the watchers inject `AuthService`, which injects the
      // router and the query client, and this token is read during HttpClient construction.
      provide: ACCESS_REVOKED_OBSERVER,
      useFactory: () => {
        const injector = inject(Injector);
        return () => injector.get(AccessRevokedWatcher).markRevoked();
      },
    },
    {
      provide: FORBIDDEN_OBSERVER,
      useFactory: () => {
        const injector = inject(Injector);
        return () => injector.get(RoleChangeWatcher).checkNow();
      },
    },
    provideAppInitializer(() => {
      inject(RoleChangeWatcher).start();
    }),
  ],
};

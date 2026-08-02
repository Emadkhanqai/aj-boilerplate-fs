import {
  ApplicationConfig,
  inject,
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
  AUTH_TOKEN_PROVIDER,
  SESSION_EXPIRED_NOTIFIER,
  TOKEN_REFRESHER,
  authInterceptor,
  envelopeInterceptor,
} from '@aj-boilerplate/data-access/api-client';
import { QUERY_CLIENT } from '@aj-boilerplate/shared/ui';
import { AuthService } from '@aj-boilerplate/auth';
import { appPreset } from '../styles/app-preset';

export const appConfig: ApplicationConfig = {
  providers: [
    provideClientHydration(withEventReplay()),
    provideBrowserGlobalErrorListeners(),
    // Every `loadComponent` route still splits out of the INITIAL bundle, but once the first
    // render is done the router preloads all of them in the background, so the first actual
    // navigation to any of them is instant instead of paying its chunk's network cost then.
    provideRouter(appRoutes, withPreloading(PreloadAllModules)),
    // Interceptor ORDER matters: `authInterceptor` must run before `envelopeInterceptor` so that
    // by the time it inspects a failure it is looking at the unwrapped `ApiError`, not a raw
    // `HttpErrorResponse`. Keep them in this order.
    provideHttpClient(withFetch(), withInterceptors([authInterceptor, envelopeInterceptor])),
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
    }),
    // The three DI seams that let `data-access` handle auth without importing `libs/auth`
    // (the module-boundary rule forbids that dependency direction).
    {
      provide: AUTH_TOKEN_PROVIDER,
      useFactory: () => {
        const auth = inject(AuthService);
        return () => auth.currentToken();
      },
    },
    {
      provide: TOKEN_REFRESHER,
      useFactory: () => {
        const auth = inject(AuthService);
        return () => auth.refreshToken();
      },
    },
    {
      provide: SESSION_EXPIRED_NOTIFIER,
      useFactory: () => {
        const auth = inject(AuthService);
        return () => auth.notifySessionExpired();
      },
    },
  ],
};

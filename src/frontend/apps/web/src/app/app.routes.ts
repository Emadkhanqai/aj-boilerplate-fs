import type { Routes } from '@angular/router';
import { authGuard, capabilityGuard } from '@aj-boilerplate/auth';
import { unsavedChangesGuard } from '@aj-boilerplate/shared/util';
import { AppLayoutComponent } from '@aj-boilerplate/shell';
import { LoginPageComponent } from './pages/login-page/login-page';
import { NotFoundPageComponent } from './pages/not-found-page/not-found-page';
import { AccessDeniedPageComponent } from './pages/access-denied-page/access-denied-page';
import { AccessCheckUnavailablePageComponent } from './pages/access-check-unavailable-page/access-check-unavailable-page';
import { SigningOutPageComponent } from './pages/signing-out-page/signing-out-page';
import { HomePageComponent } from './pages/home-page/home-page';

export const appRoutes: Routes = [
  // ---- Public routes: rendered OUTSIDE the authenticated shell. ----
  { path: 'login', component: LoginPageComponent },
  // There is deliberately NO `/auth/callback` route: the Auth service owns that path, and nginx
  // proxies `/auth/` to it, so the SPA never sees the identity provider's redirect.
  // Full-screen "Signing out…" blocker — runs auth.signOut() itself, then routes on to /login (or
  // the identity provider redirects the whole document away first). Public: it clears the session.
  { path: 'signing-out', component: SigningOutPageComponent },
  // Reached via capabilityGuard's redirect; also directly linkable/bookmarkable.
  { path: 'access-denied', component: AccessDeniedPageComponent },
  // Reached when `/api/v1/me` could not be read for a reason that is NOT the server's verdict.
  { path: 'access-check-unavailable', component: AccessCheckUnavailablePageComponent },

  // ---- Authenticated app: guarded, then wrapped in the shell. ----
  {
    path: '',
    canActivate: [authGuard],
    component: AppLayoutComponent,
    children: [
      // The landing route stays EAGER — it is what every authenticated user's first paint
      // renders, so code-splitting it would only add a network round-trip with no benefit.
      { path: '', component: HomePageComponent },

      // ---- SAMPLE FEATURE (`libs/feature-items`) — delete this block and the library. ----
      // Lazy, like every non-landing feature route should be.
      {
        path: 'items',
        loadComponent: () => import('@aj-boilerplate/feature-items').then((m) => m.ItemListPageComponent),
      },
      // Both form routes carry `unsavedChangesGuard`: it prompts (via the page's own
      // `app-confirm-dialog`) before a navigation that would discard a dirty form, and is a
      // no-op on a pristine one. See `libs/shared/util/src/lib/unsaved-changes.guard.ts`.
      {
        path: 'items/new',
        loadComponent: () => import('@aj-boilerplate/feature-items').then((m) => m.ItemFormPageComponent),
        canActivate: [capabilityGuard('canCreate')],
        canDeactivate: [unsavedChangesGuard],
      },
      {
        path: 'items/:id',
        loadComponent: () => import('@aj-boilerplate/feature-items').then((m) => m.ItemFormPageComponent),
        canActivate: [capabilityGuard('canEdit')],
        canDeactivate: [unsavedChangesGuard],
      },
      // ---- end sample feature ----

      // ---- Administration (libs/feature-admin): behind the admin capability. ----
      {
        path: 'admin/users',
        loadComponent: () => import('@aj-boilerplate/feature-admin').then((m) => m.UsersPageComponent),
        canActivate: [capabilityGuard('canAdminister')],
      },
      {
        path: 'admin/sign-in-history',
        loadComponent: () => import('@aj-boilerplate/feature-admin').then((m) => m.SignInHistoryPageComponent),
        canActivate: [capabilityGuard('canAdminister')],
      },
    ],
  },

  // Catch-all.
  { path: '**', component: NotFoundPageComponent },
];

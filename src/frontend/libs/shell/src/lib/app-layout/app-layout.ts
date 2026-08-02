import { Component, ChangeDetectionStrategy, computed, effect, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { AuthService } from '@aj-boilerplate/auth';
import { filter, map } from 'rxjs';
import { SidebarComponent } from '../sidebar/sidebar';
import { TopBarComponent } from '../top-bar/top-bar';

interface PageMeta {
  title: string;
  crumb: string;
}

/**
 * Route -> page title/breadcrumb. One mapping for the whole shell, so a page can never disagree
 * with the header above it. Extend as routes are added.
 */
function metaForPath(pathname: string): PageMeta {
  if (pathname === '/') return { title: 'Home', crumb: 'Overview' };
  if (pathname === '/items/new') return { title: 'New Item', crumb: 'Items' };
  if (pathname.startsWith('/items/')) return { title: 'Edit Item', crumb: 'Items' };
  if (pathname.startsWith('/items')) return { title: 'Items', crumb: 'Workspace' };
  if (pathname.startsWith('/settings')) return { title: 'Settings', crumb: 'Administration' };
  return { title: 'Application', crumb: '' };
}

/**
 * Authenticated app shell: sidebar + top bar + routed content. Wired as the component for the
 * `authGuard`-protected route group in `app.routes.ts`, so it always has a session.
 */
@Component({
  selector: 'app-app-layout',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, SidebarComponent, TopBarComponent],
  template: `
    <div class="app-shell">
      <app-sidebar [isOpen]="navOpen()" />
      @if (navOpen()) {
        <div class="nav-backdrop" (click)="closeNav()" aria-hidden="true"></div>
      }
      <div class="main">
        <app-top-bar [title]="meta().title" [crumb]="meta().crumb" (toggleNav)="toggleNav()" />
        <main class="content">
          <router-outlet />
        </main>
      </div>
    </div>
  `,
})
export class AppLayoutComponent {
  readonly navOpen = signal(false);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  private readonly path = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );
  readonly meta = computed(() => metaForPath(this.path().split('?')[0]));

  constructor() {
    // `authGuard` only runs on navigation — if the session expires while the user is already
    // sitting on a protected route (no navigation triggered), nothing else sends them to /login.
    // This shell is always mounted for the authenticated route group, so it is the right place to
    // react to that instead.
    effect(() => {
      if (this.auth.sessionExpired()) {
        void this.router.navigateByUrl('/login');
      }
    });
  }

  toggleNav(): void {
    this.navOpen.update((v) => !v);
  }

  closeNav(): void {
    this.navOpen.set(false);
  }
}

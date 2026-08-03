import { Component, ChangeDetectionStrategy, computed, effect, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { AuthService } from '@aj-boilerplate/auth';
import { WhatsNewModalComponent } from '@aj-boilerplate/shared/ui';
import { FeatureAnnouncementsApiService } from '@aj-boilerplate/data-access/api-client';
import type { FeatureAnnouncement } from '@aj-boilerplate/data-access/api-client';
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
  imports: [RouterOutlet, SidebarComponent, TopBarComponent, WhatsNewModalComponent],
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

    <!-- "What's new" feature spotlight. Asked for on every route change; renders only when the
         backend hands back at least one unacknowledged entry matching the current URL prefix.
         Dismissal POSTs the full id list so none of them re-surface for this user, on any device.
         It lives HERE, in the shell, and never inside a page: the check must run on every route
         change regardless of which page is mounted, and one announcement can be scoped to several
         unrelated pages. Every page gets it for free with zero per-page wiring. -->
    @if (whatsNewFeatures().length > 0) {
      <app-whats-new-modal
        [features]="whatsNewFeatures()"
        (closed)="onWhatsNewClosed($event)"
      />
    }
  `,
})
export class AppLayoutComponent {
  readonly navOpen = signal(false);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  private readonly featureAnnouncements = inject(FeatureAnnouncementsApiService);

  /** Announcements to render — an empty array means "no popup". Refilled by the effect below. */
  protected readonly whatsNewFeatures = signal<FeatureAnnouncement[]>([]);
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

    // "What's new" sweep — runs on every NavigationEnd while the user is authenticated. The
    // backend filters by path + per-user acknowledgement state, so a fresh visit only returns the
    // unread set. The request is not suppressed while a popup is already open: the endpoint is
    // cheap, and the result cannot blink the modal away (see below).
    effect(() => {
      this.path();
      if (!this.auth.isAuthenticated()) return;
      const path = typeof window !== 'undefined' ? window.location.pathname : '/';
      this.featureAnnouncements.unack(path).subscribe({
        next: (list) => {
          // Only ever SET a non-empty array here, never clear. If the backend momentarily
          // returns an empty list mid-session (a fast double-navigation racing the HTTP
          // response), an already-open modal must not flash away because a NEWER request landed
          // first with nothing pending. The modal is cleared in exactly one place:
          // `onWhatsNewClosed`, on a deliberate user dismiss.
          if (list.length > 0) this.whatsNewFeatures.set(list);
        },
        error: () => {
          // Silent by design — feature popups are non-critical UX. Never toast for this.
        },
      });
    });
  }

  /**
   * User dismissed the "What's new" modal. Clear the local signal FIRST so the popup disappears
   * immediately, then POST the acknowledgement. A failed POST still leaves it closed — the
   * backend lookup will simply re-surface it on a later navigation and the user can dismiss
   * again, which is a far better failure mode than a modal that refuses to go away.
   */
  protected onWhatsNewClosed(ids: string[]): void {
    this.whatsNewFeatures.set([]);
    if (ids.length === 0) return;
    this.featureAnnouncements.ack(ids).subscribe({
      error: () => {
        // Silent, as above.
      },
    });
  }

  toggleNav(): void {
    this.navOpen.update((v) => !v);
  }

  closeNav(): void {
    this.navOpen.set(false);
  }
}

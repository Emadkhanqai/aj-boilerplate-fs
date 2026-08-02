import { Component, ChangeDetectionStrategy, computed, inject, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { AuthService } from '@aj-boilerplate/auth';
import { initialsOf } from '@aj-boilerplate/shared/util';
import { ButtonModule } from 'primeng/button';
import { filter, map } from 'rxjs';
import { NAV_GROUPS } from '../nav-config';
import type { NavItem } from '../nav-config';

/**
 * Resolve whether a nav item is the active route. `activeWhen` wins outright; `end` items match
 * the path exactly; everything else matches the item path or a sub-path.
 *
 * Deliberately NOT delegated to `routerLinkActive`, whose subset-path matching cannot express the
 * "active on `/items` and `/items/42`, but not on `/items/new`" rule that `activeWhen` needs.
 */
function isItemActive(item: NavItem, pathname: string): boolean {
  if (item.activeWhen !== undefined) {
    return item.activeWhen(pathname);
  }
  if (item.end === true) {
    return pathname === item.to;
  }
  return pathname === item.to || pathname.startsWith(`${item.to}/`);
}

@Component({
  selector: 'app-sidebar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ButtonModule],
  template: `
    <aside [class.sidebar]="true" [class.is-open]="isOpen()" aria-label="Primary navigation">
      <div class="brand-row">
        <!-- Replace the mark and the two lines below with your product's identity. -->
        <div class="brand-mark" aria-hidden="true">AB</div>
        <div class="brand-text">
          <span class="b1">Boilerplate</span>
          <span class="b2">Application</span>
        </div>
      </div>

      <nav class="side-nav">
        @for (group of visibleGroups(); track group.label) {
          <div>
            <div class="group-label">{{ group.label }}</div>
            @for (item of group.items; track item.to) {
              <a
                [routerLink]="item.to"
                class="side-link"
                [class.active]="isActive(item)"
                [attr.aria-current]="isActive(item) ? 'page' : null"
              >
                <i [class]="item.icon" aria-hidden="true"></i>
                <span>{{ item.label }}</span>
              </a>
            }
          </div>
        }
      </nav>

      <div class="side-foot">
        <div class="user-chip">
          <div class="avatar" aria-hidden="true">{{ userInitials() }}</div>
          <div class="u-info">
            <div class="u-name">{{ userName() }}</div>
            <div class="u-role">{{ primaryRole() }}</div>
          </div>
          <p-button
            class="logout"
            icon="pi pi-sign-out"
            severity="secondary"
            [text]="true"
            [rounded]="true"
            ariaLabel="Sign out"
            (onClick)="signOut()"
          />
        </div>
      </div>
    </aside>
  `,
})
export class SidebarComponent {
  readonly isOpen = input(false);

  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  /** Reactive current pathname (query/hash stripped), recomputed on every completed navigation. */
  private readonly currentPathname = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map((event) => event.urlAfterRedirects.split(/[?#]/)[0]),
    ),
    { initialValue: this.router.url.split(/[?#]/)[0] },
  );

  readonly visibleGroups = computed(() =>
    NAV_GROUPS.map((group) => ({
      ...group,
      items: group.items.filter((item: NavItem) => this.isVisible(item)),
    })).filter((group) => group.items.length > 0),
  );

  readonly userName = computed(() => this.auth.user()?.name ?? 'Unknown');
  readonly userInitials = computed(() => initialsOf(this.userName()));
  readonly primaryRole = computed(() => this.auth.roles()[0] ?? '—');

  private isVisible(item: NavItem): boolean {
    return item.requiredCapability === undefined || this.auth.capabilities()[item.requiredCapability];
  }

  isActive(item: NavItem): boolean {
    return isItemActive(item, this.currentPathname());
  }

  signOut(): void {
    // Hand off to the full-screen "Signing out…" page: navigating there unmounts the shell in one
    // tick (covering the whole UI immediately), and that page runs `auth.signOut()` itself, so it
    // also covers the unbounded identity-provider logout redirect before `/login`.
    void this.router.navigateByUrl('/signing-out');
  }
}

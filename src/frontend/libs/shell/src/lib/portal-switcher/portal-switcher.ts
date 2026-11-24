import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ButtonModule } from 'primeng/button';
import { MenuModule } from 'primeng/menu';
import type { MenuItem } from 'primeng/api';
import { injectQuery } from '@tanstack/angular-query-experimental';
import { firstValueFrom } from 'rxjs';
import { AdminApiService } from '@aj-boilerplate/data-access/api-client';
import { sortByLabel } from '@aj-boilerplate/shared/util';

/**
 * Links to the other portals this person may open (`GET /api/v1/me/portals`). One portal is just
 * "here", so with 0 or 1 the control is not rendered at all; a failed read also hides it — a
 * missing shortcut must never become an error banner on every page.
 */
@Component({
  selector: 'app-portal-switcher',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ButtonModule, MenuModule],
  template: `
    @if (items().length > 1) {
      <p-button
        label="Portals"
        icon="pi pi-th-large"
        severity="secondary"
        [text]="true"
        [style]="{ height: 'var(--control-h)' }"
        data-testid="portal-switcher"
        ariaLabel="Switch portal"
        (onClick)="menu.toggle($event)"
      />
      <p-menu #menu [model]="items()" [popup]="true" appendTo="body" />
    }
  `,
})
export class PortalSwitcherComponent {
  private readonly api = inject(AdminApiService);

  private readonly query = injectQuery(() => ({
    queryKey: ['me', 'portals'],
    queryFn: () => firstValueFrom(this.api.myPortals()),
    meta: { suppressGlobalToast: true },
    retry: false,
  }));

  protected readonly items = computed<MenuItem[]>(() =>
    sortByLabel(this.query.data()?.portals ?? [], (p) => p.name).map((p) => ({ label: p.name, url: p.url })),
  );
}

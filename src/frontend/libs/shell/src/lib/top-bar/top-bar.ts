import { Component, ChangeDetectionStrategy, computed, inject, input, output } from '@angular/core';
import { ButtonModule } from 'primeng/button';
import { AuthService } from '@aj-boilerplate/auth';

/**
 * The authenticated shell's header: nav toggle (small screens), page title + breadcrumb, and the
 * user's role. Title and crumb are inputs rather than derived here, so the layout owns the
 * route -> title mapping in one place.
 */
@Component({
  selector: 'app-top-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ButtonModule],
  template: `
    <header class="topbar">
      <p-button
        class="nav-toggle"
        icon="pi pi-bars"
        severity="secondary"
        [text]="true"
        [rounded]="true"
        ariaLabel="Toggle navigation"
        (onClick)="toggleNav.emit()"
      />
      <div>
        @if (crumb(); as c) {
          <div class="crumb">{{ c }}</div>
        }
        <h1>{{ title() }}</h1>
      </div>
      <span class="spacer"></span>
      <div class="header-actions">
        @if (primaryRole(); as role) {
          <span class="pill-role">{{ role }}</span>
        }
        <p-button
          icon="pi pi-bell"
          severity="secondary"
          [text]="true"
          [rounded]="true"
          ariaLabel="Notifications"
        />
      </div>
    </header>
  `,
})
export class TopBarComponent {
  readonly title = input.required<string>();
  readonly crumb = input<string>();
  readonly toggleNav = output<void>();

  private readonly auth = inject(AuthService);

  readonly primaryRole = computed(() => this.auth.roles()[0]);
}

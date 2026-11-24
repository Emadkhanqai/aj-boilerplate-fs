import { ChangeDetectionStrategy, Component, InjectionToken, inject } from '@angular/core';
import { LocationStrategy } from '@angular/common';
import { DialogModule } from 'primeng/dialog';
import { ButtonModule } from 'primeng/button';
import { RoleChangeWatcher } from './role-change-watcher';

/** Seam for the full-page reload; tests replace it because jsdom's `location.assign` cannot be spied on. */
export const ROLE_CHANGE_NAVIGATE = new InjectionToken<(url: string) => void>('ROLE_CHANGE_NAVIGATE', {
  providedIn: 'root',
  factory: () => (url: string) => window.location.assign(url),
});

/**
 * Blocking dialog shown when the user LOST capabilities while the app was open. The server already
 * enforces the narrower set, so whatever is on screen may be something they can no longer use; the
 * only way forward is a full reload, which rebuilds every route and menu from the new profile.
 * Not dismissable on purpose: closing it would leave the stale screen in place.
 */
@Component({
  selector: 'app-role-change-dialog',
  imports: [DialogModule, ButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (watcher.blocking()) {
      <p-dialog
        [visible]="true"
        [modal]="true"
        [closable]="false"
        [closeOnEscape]="false"
        [dismissableMask]="false"
        [draggable]="false"
        [resizable]="false"
        [role]="'alertdialog'"
        [style]="{ width: '440px' }"
      >
        <ng-template pTemplate="header">
          <h2>Your access has changed</h2>
        </ng-template>

        <p data-testid="role-change-message">
          Your permissions were changed while you were using the app. Reload to continue with your
          current access.
        </p>

        <ng-template pTemplate="footer">
          <p-button label="Reload" data-testid="role-change-reload" [autofocus]="true" (onClick)="reload()" />
        </ng-template>
      </p-dialog>
    }
  `,
})
export class RoleChangeDialogComponent {
  protected readonly watcher = inject(RoleChangeWatcher);
  private readonly locationStrategy = inject(LocationStrategy);
  private readonly navigate = inject(ROLE_CHANGE_NAVIGATE);

  reload(): void {
    this.navigate(this.locationStrategy.prepareExternalUrl('/'));
  }
}

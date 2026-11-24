import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { DialogModule } from 'primeng/dialog';
import { ButtonModule } from 'primeng/button';
import { AuthService } from '@aj-boilerplate/auth';
import { AccessRevokedWatcher } from './access-revoked-watcher';

/**
 * Blocking dialog for a definitive "you no longer have access here" verdict. Its ONLY action is to
 * sign out: retrying cannot succeed, and sending the user to `/login` would just start a sign-in
 * that ends in the same refusal. `signOut()` POSTs `/auth/logout` and lands on `/logged-out`.
 */
@Component({
  selector: 'app-access-revoked-dialog',
  imports: [DialogModule, ButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (watcher.revoked()) {
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
          <h2>Your access has been removed</h2>
        </ng-template>

        <p data-testid="access-revoked-message">Contact your administrator if you think this is a mistake.</p>

        <ng-template pTemplate="footer">
          <p-button label="Sign out" data-testid="access-revoked-sign-out" [autofocus]="true" (onClick)="signOut()" />
        </ng-template>
      </p-dialog>
    }
  `,
})
export class AccessRevokedDialogComponent {
  protected readonly watcher = inject(AccessRevokedWatcher);
  private readonly auth = inject(AuthService);

  signOut(): void {
    this.auth.signOut();
  }
}

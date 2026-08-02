import { Component, ChangeDetectionStrategy, input, output } from '@angular/core';
import { DialogModule } from 'primeng/dialog';
import { ButtonModule } from 'primeng/button';

/**
 * The application's own yes/no confirmation dialog, in place of the browser's native
 * `confirm()` — which cannot be styled, cannot show a busy state, and blocks the event loop.
 *
 * Callers hold their own open signal and render this conditionally with `@if`, so the dialog has
 * no internal open/close state to get out of sync with the caller's.
 */
@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DialogModule, ButtonModule],
  templateUrl: './confirm-dialog.html',
})
export class ConfirmDialogComponent {
  readonly title = input.required<string>();
  readonly message = input.required<string>();
  readonly confirmLabel = input('Confirm');
  readonly danger = input(false);
  readonly busy = input(false);
  /** Set after a failed `confirm` — shown inline, right where the user is already looking,
   * instead of a second modal or a toast that lands somewhere else on screen. */
  readonly error = input<string | null>(null);
  readonly confirm = output<void>();
  // `close` matches the native DOM `close` event name, but it is the interface contract callers
  // bind against (`(close)="..."`) — renaming it would break that wiring.
  // eslint-disable-next-line @angular-eslint/no-output-native
  readonly close = output<void>();

  protected onConfirm(): void {
    this.confirm.emit();
  }

  protected onClose(): void {
    this.close.emit();
  }
}

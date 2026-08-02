import { Component, ChangeDetectionStrategy, input } from '@angular/core';

/**
 * The "nothing to show" panel body. Every data view owes the user four states — loading, error,
 * empty, and success — and this is the third one. Project the call-to-action (a `p-button`, a
 * router link) as the component's content.
 */
@Component({
  selector: 'app-empty-state',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="empty-state">
      <h3>{{ heading() }}</h3>
      @if (message(); as m) {
        <p>{{ m }}</p>
      }
      <div class="empty-state-action">
        <ng-content />
      </div>
    </div>
  `,
})
export class EmptyStateComponent {
  readonly heading = input.required<string>();
  readonly message = input<string>();
}

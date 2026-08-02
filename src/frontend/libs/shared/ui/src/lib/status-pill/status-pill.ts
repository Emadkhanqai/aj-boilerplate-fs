import { Component, ChangeDetectionStrategy, computed, input } from '@angular/core';

/**
 * The tones the design system's `.s-*` classes provide (see `components.css`). Add a tone here
 * only when you add the matching CSS class.
 */
type StatusTone = 'draft' | 'submitted' | 'review' | 'revision' | 'approved' | 'rejected' | 'published' | 'neutral';

/**
 * Maps a status VALUE to a tone. This is the one place status colour is decided — never
 * hard-code a `.s-*` class at a call site, or two screens will eventually disagree about what
 * "Archived" looks like.
 */
function toneFor(status: string): StatusTone {
  switch (status) {
    case 'Active':
      return 'approved';
    case 'Draft':
      return 'draft';
    case 'Archived':
      return 'neutral';
    default:
      return 'neutral';
  }
}

/** Status pill. Renders the status text in the tone `toneFor` assigns it. */
@Component({
  selector: 'app-status-pill',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="status" [class]="'s-' + tone()">{{ status() }}</span>`,
})
export class StatusPillComponent {
  readonly status = input.required<string>();
  readonly tone = computed(() => toneFor(this.status()));
}

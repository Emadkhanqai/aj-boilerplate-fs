import { Injectable, inject } from '@angular/core';
import { Title } from '@angular/platform-browser';

/**
 * Thin wrapper around Angular's `Title` service. Each page component calls `set(...)` in its
 * own `ngOnInit` — no restore-on-unmount behaviour: this SPA never has two page components mounted simultaneously
 * outside a route transition, and the next page's `ngOnInit` always sets a fresh title
 * immediately, so a simple set-on-init is behaviorally equivalent here.
 */
@Injectable({ providedIn: 'root' })
export class DocumentTitleService {
  private readonly title = inject(Title);

  set(title: string): void {
    this.title.setTitle(title);
  }
}

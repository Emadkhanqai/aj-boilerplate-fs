import { Injectable, signal } from '@angular/core';

/**
 * Holds the one fact "the server told us this user's access was revoked" (`403` plus
 * `X-Usp-Access: revoked`). Bound to `ACCESS_REVOKED_OBSERVER` in `app.config.ts`. Latched: the
 * only way out is signing out, which reloads the document.
 */
@Injectable({ providedIn: 'root' })
export class AccessRevokedWatcher {
  private readonly revokedSignal = signal(false);
  readonly revoked = this.revokedSignal.asReadonly();

  markRevoked(): void {
    this.revokedSignal.set(true);
  }
}

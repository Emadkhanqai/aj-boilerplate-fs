import { Component, ChangeDetectionStrategy, OnDestroy, OnInit, inject } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '@aj-boilerplate/auth';
import { DocumentTitleService } from '@aj-boilerplate/shared/util';

/**
 * The full-screen "Signing out…" blocker — the mirror of the sign-in callback's wait.
 *
 * Why a dedicated ROUTE rather than an inline overlay: navigating here unmounts the authenticated
 * shell in one tick, so the whole UI is covered the instant the user clicks "Sign out" — and it
 * stays covered across the two very different exits that follow. In dev mode `auth.signOut()` is
 * synchronous (it just clears local storage), so nothing would otherwise be seen; the
 * {@link HOLD_MS} floor keeps this page up long enough to read before routing on to `/login`. In
 * gateway mode `auth.signOut()` POSTs `/auth/logout` and then leaves the document for
 * `/logged-out` — a round trip of unbounded length that would otherwise flash a half-torn-down UI.
 */
@Component({
  selector: 'app-signing-out-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="auth-cb">
      <div class="auth-cb-stack">
        <div class="auth-cb-card">
          <span class="auth-cb-mark" aria-hidden="true"><i class="pi pi-sign-out"></i></span>
          <div class="auth-cb-status" role="status" aria-live="polite">
            <h3>Signing out…</h3>
            <p class="auth-cb-sub">Ending your session securely.</p>
          </div>
          <div class="auth-cb-rail" aria-hidden="true">
            <span></span><span></span><span></span>
          </div>
          <ol class="auth-cb-phases">
            <li>Ending session</li>
            <li>Clearing this device</li>
            <li>Returning to sign-in</li>
          </ol>
        </div>
      </div>
    </div>
  `,
})
export class SigningOutPageComponent implements OnInit, OnDestroy {
  /** Minimum time this page stays up before routing on to `/login`, so an instant (dev-mode)
   * sign-out still shows the screen rather than flashing it. A real identity provider redirects
   * the whole document away before this elapses, so the floor never delays it. */
  private static readonly HOLD_MS = 1100;

  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly documentTitle = inject(DocumentTitleService);
  private timer: ReturnType<typeof setTimeout> | undefined;

  ngOnInit(): void {
    this.documentTitle.set('Signing out… · Application');
    // Clears the session signal + query cache and fires the provider's own sign-out
    // (synchronous for dev; for the gateway, a POST to /auth/logout and then a full-document
    // navigation to /logged-out).
    this.auth.signOut();
    // Gateway mode must NOT route on to /login: the provider is already leaving the document for
    // /logged-out, and a SPA navigation here would flash the sign-in form (and, on a slow POST,
    // race it). Only dev mode, where nothing leaves the page, needs the timer.
    if (this.auth.mode === 'dev') {
      this.timer = setTimeout(() => {
        void this.router.navigateByUrl('/login');
      }, SigningOutPageComponent.HOLD_MS);
    }
  }

  ngOnDestroy(): void {
    if (this.timer !== undefined) {
      clearTimeout(this.timer);
    }
  }
}

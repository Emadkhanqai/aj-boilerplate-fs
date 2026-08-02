import { Component, ChangeDetectionStrategy, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { completeOidcSignIn, sanitizeReturnPath } from '@aj-boilerplate/auth';
import { DocumentTitleService } from '@aj-boilerplate/shared/util';

/**
 * Lands the identity provider's redirect back (`?code=...&state=...`), completes the PKCE
 * exchange, and sends the user on to wherever `signIn()` originally captured as `from`.
 *
 * A full-page `window.location.assign` (rather than `Router.navigateByUrl`) is used deliberately:
 * the freshly-established session lives in `localStorage`, and `AuthService`'s provider-driven
 * `restore()` only ever runs once, at construction — a full reload is what makes the
 * newly-signed-in session actually stick.
 */
@Component({
  selector: 'app-auth-callback-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ButtonModule],
  template: `
    <div class="auth-cb">
      <div class="auth-cb-stack">
        <div class="auth-cb-card">
          @if (error(); as message) {
            <span class="auth-cb-mark auth-cb-mark-fail" aria-hidden="true">
              <i class="pi pi-exclamation-triangle"></i>
            </span>
            <h3>Sign-in failed</h3>
            <p class="auth-cb-sub">
              We could not complete the secure hand-back from your identity provider. Your session
              was not started.
            </p>
            <p role="alert" class="login-error">
              <i class="pi pi-exclamation-circle" aria-hidden="true"></i>
              <span>{{ message }}</span>
            </p>
            <p-button
              label="Back to sign in"
              icon="pi pi-arrow-left"
              styleClass="btn-block auth-cb-action"
              routerLink="/login"
            />
          } @else {
            <span class="auth-cb-mark" aria-hidden="true"><i class="pi pi-lock"></i></span>
            <div class="auth-cb-status" role="status" aria-live="polite">
              <h3>Signing you in…</h3>
              <p class="auth-cb-sub">Completing secure sign-in.</p>
            </div>
            <!-- Deliberately indeterminate: the exchange has no honest progress to report, and a
                 fabricated step counter would be a lie the user can catch. -->
            <div class="auth-cb-rail" aria-hidden="true">
              <span></span><span></span><span></span>
            </div>
            <ol class="auth-cb-phases">
              <li>Verifying response</li>
              <li>Exchanging code</li>
              <li>Opening workspace</li>
            </ol>
            <p class="auth-cb-foot">
              <i class="pi pi-shield" aria-hidden="true"></i>
              <span>Secure single sign-on</span>
            </p>
          }
        </div>
      </div>
    </div>
  `,
})
export class AuthCallbackPageComponent implements OnInit {
  private readonly documentTitle = inject(DocumentTitleService);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.documentTitle.set('Signing in… · Application');
    completeOidcSignIn()
      .then(({ from }) => {
        // `from` is round-tripped through sessionStorage across the redirect, so it is
        // re-sanitized here rather than trusted as already-safe — belt and braces alongside the
        // sanitization `signIn()` already applied before stashing it.
        window.location.assign(sanitizeReturnPath(from));
      })
      .catch((err: unknown) => {
        this.error.set(err instanceof Error ? err.message : 'Sign-in failed.');
      });
  }
}

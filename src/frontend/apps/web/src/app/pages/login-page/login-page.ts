import { Component, ChangeDetectionStrategy, OnInit, inject, signal } from '@angular/core';
import { Router, ActivatedRoute } from '@angular/router';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { Message } from 'primeng/message';
import { AuthService, claimGatewayAutoSignIn, isWellFormedEmail, sanitizeReturnPath } from '@aj-boilerplate/auth';
import { DocumentTitleService, initialsOf } from '@aj-boilerplate/shared/util';

/**
 * Public sign-in screen, served by nginx WITHOUT a session (`location = /login`).
 *
 * `gateway` mode: the gateway answers an unauthenticated navigation with `302 /login?from=<path>`,
 * so a visitor lands here. When `from` is present the page starts the sign-in itself, once per
 * five minutes per tab (`claimGatewayAutoSignIn` is the brake against a redirect loop); otherwise,
 * or when the brake holds, the visitor presses the button. An email is optional and, when well
 * formed, is passed to the identity provider as `login_hint`. Sign-in is a full-page navigation to
 * `/auth/login`, so it never resolves here.
 *
 * `dev` mode renders the role picker (choose a sample user) and routes to `from` or home.
 *
 * The hero copy is placeholder — replace it with your product's own.
 */
@Component({
  selector: 'app-login-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ButtonModule, InputTextModule, Message, ReactiveFormsModule],
  template: `
    <div class="login-wrap">
      <aside class="login-hero">
        <div class="login-hero-inner">
          <p class="eyebrow">Your Organisation</p>
          <h1>Application Name</h1>
          <p class="lede">
            A one-line description of what this application does and who it is for. Replace this
            copy, the eyebrow above it, and the chips below.
          </p>
          <ul class="hero-chips">
            <li>Fast</li>
            <li>Accessible</li>
            <li>Auditable</li>
          </ul>
          <p class="stamp">
            <i class="pi pi-shield" aria-hidden="true"></i>
            <span>Secure single sign-on</span>
          </p>
        </div>
      </aside>

      <main class="login-panel">
        <div class="login-panel-inner">
          <span class="login-mark" aria-hidden="true"><i class="pi pi-lock"></i></span>
          <h2>{{ auth.mode === 'dev' ? 'Choose a demo profile' : 'Sign in' }}</h2>
          <p class="sub">
            {{
              auth.mode === 'dev'
                ? 'Development sign-in — pick a role to explore its capabilities. No password required.'
                : 'Continue with your organisational account.'
            }}
          </p>

          @if (auth.sessionExpired()) {
            <p-message severity="warn" [closable]="false" styleClass="login-alert">
              Your session has expired. Please sign in again.
            </p-message>
          }

          @if (autoSignInHeld()) {
            <p-message severity="info" [closable]="false" styleClass="login-alert" data-testid="auto-sign-in-held">
              We could not complete sign-in automatically. Please use the button below to try again.
            </p-message>
          }

          @if (error(); as message) {
            <p role="alert" class="login-error">
              <i class="pi pi-exclamation-circle" aria-hidden="true"></i>
              <span>{{ message }}</span>
            </p>
          }

          @if (auth.mode === 'dev' && auth.devUsers; as users) {
            <div class="role-list">
              @for (user of users; track user.userId) {
                <p-button
                  styleClass="role-card"
                  [disabled]="pendingId() !== null"
                  [ariaLabel]="'Sign in as ' + user.name"
                  (onClick)="signIn(user.userId)"
                >
                  <span class="avatar ra-{{ user.role }}" aria-hidden="true">{{ initials(user.name) }}</span>
                  <span class="who">
                    <span class="nm">{{ user.name }}</span>
                    <span class="rl">{{ user.role }}</span>
                  </span>
                  <span class="arrow" aria-hidden="true">›</span>
                </p-button>
              }
            </div>
          } @else {
            <div class="field">
              <label for="login-email">Work email (optional)</label>
              <input
                id="login-email"
                pInputText
                type="email"
                autocomplete="username"
                data-testid="login-email"
                [formControl]="email"
                (keydown.enter)="signIn(undefined, email.value)"
              />
              <span class="hint">Pre-fills the sign-in page of your identity provider.</span>
            </div>
            <p-button
              label="Sign in with single sign-on"
              [icon]="pendingId() === null ? 'pi pi-sign-in' : 'pi pi-spin pi-spinner'"
              styleClass="btn-block login-cta"
              [disabled]="pendingId() !== null"
              (onClick)="signIn(undefined, email.value)"
            />
          }

          <p class="login-foot">
            <i class="pi pi-verified" aria-hidden="true"></i>
            <span>Access is governed by your role. All permissions are enforced server-side.</span>
          </p>
        </div>
      </main>
    </div>
  `,
})
export class LoginPageComponent implements OnInit {
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly documentTitle = inject(DocumentTitleService);

  protected readonly pendingId = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly autoSignInHeld = signal(false);
  protected readonly email = new FormControl('', { nonNullable: true });

  ngOnInit(): void {
    this.documentTitle.set('Sign in · Application');
    this.autoSignIn();
  }

  /**
   * Starts the gateway sign-in without a click. Only when the gateway sent us here (`from` is
   * present), only in gateway mode, never right after a session expired (the user should read the
   * notice first), and only if `claimGatewayAutoSignIn` grants the one attempt per window.
   */
  private autoSignIn(): void {
    if (this.auth.mode !== 'gateway' || !this.route.snapshot.queryParamMap.has('from') || this.auth.sessionExpired()) {
      return;
    }
    if (!claimGatewayAutoSignIn()) {
      this.autoSignInHeld.set(true);
      return;
    }
    void this.signIn();
  }

  protected initials(name: string): string {
    return initialsOf(name);
  }

  async signIn(userId?: string, loginHint?: string): Promise<void> {
    this.error.set(null);
    // An address that is not an email is dropped, not sent: the hint is a convenience.
    const hint = loginHint !== undefined && isWellFormedEmail(loginHint) ? loginHint : undefined;
    this.pendingId.set(userId ?? '__sso__');
    try {
      await this.auth.signIn(userId, hint);
      // Only dev mode reaches here (gateway sign-in never resolves). `from` is still the raw,
      // unauthenticated query param, so sanitize it before navigating.
      const from = sanitizeReturnPath(this.route.snapshot.queryParamMap.get('from') ?? '/');
      void this.router.navigateByUrl(from);
    } catch (err) {
      this.error.set(err instanceof Error ? err.message : 'Sign-in failed.');
      this.pendingId.set(null);
    }
  }
}

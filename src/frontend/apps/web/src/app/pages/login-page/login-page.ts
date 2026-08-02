import { Component, ChangeDetectionStrategy, OnInit, inject, signal } from '@angular/core';
import { Router, ActivatedRoute } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { Message } from 'primeng/message';
import { AuthService, sanitizeReturnPath } from '@aj-boilerplate/auth';
import { DocumentTitleService, initialsOf } from '@aj-boilerplate/shared/util';

/**
 * Public sign-in screen. In `dev` mode it renders the role picker (choose a sample user); in
 * `oidc` mode it offers a single sign-on button. Signing in persists the session and routes to
 * the originally-requested page (the `from` query param `authGuard` set) or home.
 *
 * The hero copy is placeholder — replace it with your product's own.
 */
@Component({
  selector: 'app-login-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ButtonModule, Message],
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
            <p-button
              label="Sign in with single sign-on"
              [icon]="pendingId() === null ? 'pi pi-sign-in' : 'pi pi-spin pi-spinner'"
              styleClass="btn-block login-cta"
              [disabled]="pendingId() !== null"
              (onClick)="signIn()"
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

  ngOnInit(): void {
    this.documentTitle.set('Sign in · Application');
  }

  protected initials(name: string): string {
    return initialsOf(name);
  }

  async signIn(userId?: string): Promise<void> {
    this.error.set(null);
    this.pendingId.set(userId ?? '__sso__');
    try {
      await this.auth.signIn(userId);
      // Dev-mode sign-in never leaves the app (no identity-provider round trip), so `from` is
      // still the raw, unauthenticated query param — sanitize it before navigating, exactly as
      // the real-provider callback path does for its round-tripped value.
      const from = sanitizeReturnPath(this.route.snapshot.queryParamMap.get('from') ?? '/');
      void this.router.navigateByUrl(from);
    } catch (err) {
      this.error.set(err instanceof Error ? err.message : 'Sign-in failed.');
      this.pendingId.set(null);
    }
  }
}

import { Component, ChangeDetectionStrategy, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { Message } from 'primeng/message';
import { AuthService, sanitizeReturnPath } from '@aj-boilerplate/auth';
import { apiErrorMessage } from '@aj-boilerplate/data-access/api-client';
import { DocumentTitleService } from '@aj-boilerplate/shared/util';

/**
 * Shown when `GET /api/v1/me` could not be read for a reason that is NOT the server's verdict —
 * a 5xx, a 503 from the gateway's auth check, a network failure. "No access" would be a false
 * statement: the server simply did not answer, so we stop instead of guessing. Nothing here
 * suggests anything is wrong with the account or the sign-in.
 *
 * "Try again" re-reads the profile and, when it answers, returns to the page the user wanted
 * (`from`, sanitized). Signing out is always available.
 */
@Component({
  selector: 'app-access-check-unavailable-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ButtonModule, Message, RouterLink],
  template: `
    <div class="content" style="margin-inline: auto">
      <section class="panel">
        <div class="panel-body">
          <div class="empty-state">
            <p-message severity="warn" [closable]="false" styleClass="w-full">Access check unavailable</p-message>
            <h3 style="margin-top: 16px">We couldn't check your access</h3>
            <p>
              This is not a permission problem. The server did not answer when we asked what you can
              open, so we stopped here instead of guessing. Nothing is wrong with your account or
              your sign-in.
            </p>
            @if (reason(); as text) {
              <p data-testid="unavailable-reason">{{ text }}</p>
            }
            @if (retryFailed()) {
              <p-message severity="error" [closable]="false" styleClass="w-full" data-testid="unavailable-still-failing">
                Still no answer. The service may be busy — wait a moment, then try again.
              </p-message>
            }
            <div class="form-actions">
              <p-button
                [label]="retrying() ? 'Checking…' : 'Try again'"
                icon="pi pi-refresh"
                [loading]="retrying()"
                data-testid="unavailable-retry"
                (onClick)="retry()"
              />
              <a routerLink="/signing-out">Sign out</a>
            </div>
          </div>
        </div>
      </section>
    </div>
  `,
})
export class AccessCheckUnavailablePageComponent implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly documentTitle = inject(DocumentTitleService);
  // Read reactively: Angular reuses this component instance when only the query string changes.
  private readonly queryParams = toSignal(inject(ActivatedRoute).queryParamMap);

  protected readonly retrying = signal(false);
  protected readonly retryFailed = signal(false);

  private readonly returnTo = computed(() => sanitizeReturnPath(this.queryParams()?.get('from') ?? '/'));

  protected readonly reason = computed(() => {
    const error: unknown = this.auth.capabilitiesError();
    return error === null || error === undefined
      ? null
      : apiErrorMessage(error, 'The access check could not be completed');
  });

  ngOnInit(): void {
    this.documentTitle.set('Access check unavailable · Application');
  }

  protected async retry(): Promise<void> {
    if (this.retrying()) {
      return;
    }
    this.retrying.set(true);
    this.retryFailed.set(false);
    try {
      if (await this.auth.refreshCapabilities()) {
        await this.router.navigateByUrl(this.returnTo());
        return;
      }
      this.retryFailed.set(true);
    } finally {
      this.retrying.set(false);
    }
  }
}

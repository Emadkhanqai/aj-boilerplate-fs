import { Component, ChangeDetectionStrategy, OnInit, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Message } from 'primeng/message';
import { DocumentTitleService } from '@aj-boilerplate/shared/util';

/**
 * Visible "access denied" page — where `capabilityGuard` sends a user who lacks the capability a
 * route requires. A visible refusal beats a silent redirect: the user learns the page exists and
 * that their role is the reason they cannot see it.
 *
 * The backend remains the real enforcement point; this is UX only.
 */
@Component({
  selector: 'app-access-denied-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, Message],
  template: `
    <div class="content" style="margin-inline: auto">
      <section class="panel">
        <div class="panel-body">
          <div class="empty-state">
            <p-message severity="error" [closable]="false" styleClass="w-full">Access denied</p-message>
            <h3 style="margin-top: 16px">You don't have access to this page</h3>
            <p>
              Your current role does not include the permission needed to view this page. Contact
              an administrator if you believe this is incorrect.
            </p>
            <p style="margin-top: 14px">
              <a routerLink="/">Return home</a>
            </p>
          </div>
        </div>
      </section>
    </div>
  `,
})
export class AccessDeniedPageComponent implements OnInit {
  private readonly documentTitle = inject(DocumentTitleService);

  ngOnInit(): void {
    this.documentTitle.set('Access denied · Application');
  }
}

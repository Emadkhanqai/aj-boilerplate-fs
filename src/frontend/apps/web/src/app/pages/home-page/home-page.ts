import { Component, ChangeDetectionStrategy, OnInit, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { AuthService } from '@aj-boilerplate/auth';
import { DocumentTitleService } from '@aj-boilerplate/shared/util';

/**
 * The landing page for an authenticated session. Replace this with your product's dashboard —
 * it exists so the routed shell has somewhere to land, and to show the greeting/panel classes
 * the design system provides.
 */
@Component({
  selector: 'app-home-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ButtonModule],
  template: `
    <div class="greeting">
      <h2>Welcome, {{ userName() }}</h2>
      <p class="sub">You are signed in as <strong>{{ role() }}</strong>.</p>
    </div>

    <section class="panel">
      <div class="panel-head">
        <h3>Getting started</h3>
        <p class="sub">This page, and the Items feature it links to, are sample content.</p>
      </div>
      <div class="panel-body">
        <p>
          The <strong>Items</strong> feature (<code>libs/feature-items</code>) is a complete
          vertical slice: a paged, searchable list and a validated create/edit form, wired to
          <code>/api/v1/items</code> through the generated API types. Read it, copy the shape, then
          delete it.
        </p>
        <p style="margin-top: 12px">
          Before building any UI, read <code>DESIGN.md</code> and <code>CLAUDE.md</code> at the
          frontend root.
        </p>
        <div class="form-actions">
          <p-button label="Browse items" icon="pi pi-box" routerLink="/items" />
        </div>
      </div>
    </section>
  `,
})
export class HomePageComponent implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly documentTitle = inject(DocumentTitleService);

  protected userName(): string {
    return this.auth.user()?.name ?? 'there';
  }

  protected role(): string {
    return this.auth.roles()[0] ?? 'no role';
  }

  ngOnInit(): void {
    this.documentTitle.set('Home · Application');
  }
}

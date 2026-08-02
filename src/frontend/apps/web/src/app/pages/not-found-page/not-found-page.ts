import { Component, ChangeDetectionStrategy, OnInit, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DocumentTitleService } from '@aj-boilerplate/shared/util';

/** Catch-all 404. */
@Component({
  selector: 'app-not-found-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <div class="content" style="margin-inline: auto">
      <section class="panel">
        <div class="panel-body">
          <div class="empty-state">
            <h3>Page not found</h3>
            <p>The page you are looking for does not exist or has moved.</p>
            <p style="margin-top: 14px">
              <a routerLink="/">Return home</a>
            </p>
          </div>
        </div>
      </section>
    </div>
  `,
})
export class NotFoundPageComponent implements OnInit {
  private readonly documentTitle = inject(DocumentTitleService);

  ngOnInit(): void {
    this.documentTitle.set('Not found · Application');
  }
}

import { Component, ChangeDetectionStrategy, computed, inject, input, output } from '@angular/core';
import { ButtonModule } from 'primeng/button';
import { AuthService } from '@aj-boilerplate/auth';
import { LanguageService } from '@aj-boilerplate/shared/util';

/**
 * The authenticated shell's header: nav toggle (small screens), page title + breadcrumb, the
 * language toggle, and the user's role. Title and crumb are inputs rather than derived here, so
 * the layout owns the route -> title mapping in one place.
 */
@Component({
  selector: 'app-top-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ButtonModule],
  template: `
    <header class="topbar">
      <p-button
        class="nav-toggle"
        icon="pi pi-bars"
        severity="secondary"
        [text]="true"
        [rounded]="true"
        ariaLabel="Toggle navigation"
        (onClick)="toggleNav.emit()"
      />
      <div>
        @if (crumb(); as c) {
          <div class="crumb">{{ c }}</div>
        }
        <h1>{{ title() }}</h1>
      </div>
      <span class="spacer"></span>
      <div class="header-actions">
        @if (primaryRole(); as role) {
          <span class="pill-role">{{ role }}</span>
        }
        <!-- Language toggle. Switching to Arabic flips the document to dir="rtl" via
             DocumentDirectionService, mirroring the whole layout. It does NOT translate the UI —
             only bilingual DATA (see LanguageService.pick) changes language. ADR-0010 explains
             that boundary. The label is the target language written in its own script, the one
             convention every locale switcher shares.
             (No backticks in this comment: the template is a template literal.) -->
        <p-button
          [label]="otherLanguageLabel()"
          severity="secondary"
          [text]="true"
          size="small"
          data-testid="language-toggle"
          [ariaLabel]="'Switch to ' + otherLanguageName()"
          (onClick)="toggleLanguage()"
        />
        <p-button
          icon="pi pi-bell"
          severity="secondary"
          [text]="true"
          [rounded]="true"
          ariaLabel="Notifications"
        />
      </div>
    </header>
  `,
})
export class TopBarComponent {
  readonly title = input.required<string>();
  readonly crumb = input<string>();
  readonly toggleNav = output<void>();

  private readonly auth = inject(AuthService);
  private readonly language = inject(LanguageService);

  readonly primaryRole = computed(() => this.auth.roles()[0]);

  /** The language the toggle switches TO, labelled in its own script. */
  readonly otherLanguageLabel = computed(() => (this.language.current() === 'en' ? 'العربية' : 'English'));

  /** English name of that language, for the button's accessible label — a screen reader
   * announcing this control is still reading an otherwise-English interface. */
  readonly otherLanguageName = computed(() => (this.language.current() === 'en' ? 'Arabic' : 'English'));

  protected toggleLanguage(): void {
    this.language.set(this.language.current() === 'en' ? 'ar' : 'en');
  }
}

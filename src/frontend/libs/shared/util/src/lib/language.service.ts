import { Injectable, signal } from '@angular/core';

/** The locales the UI can present. English is the default; Arabic is wired but not yet exposed. */
export type AppLanguage = 'en' | 'ar';

/**
 * The minimum viable bilingual seam: which language the UI is currently showing, plus one
 * `pick` helper for choosing between a paired English/Arabic value.
 *
 * This is deliberately NOT an i18n framework. The workspace has no Transloco/`$localize` setup
 * and this service is not an attempt to grow one — it exists because API payloads carry paired
 * `*En`/`*Ar` fields (see `FeatureAnnouncement`) and something has to decide which one renders.
 * If the product ever needs real message catalogues, plural rules, or date/number localisation,
 * adopt a proper library and delete this.
 */
@Injectable({ providedIn: 'root' })
export class LanguageService {
  private readonly languageSignal = signal<AppLanguage>('en');

  /** The language the UI is rendering right now. Read it in a template to re-render on change. */
  readonly current = this.languageSignal.asReadonly();

  set(language: AppLanguage): void {
    this.languageSignal.set(language);
  }

  /**
   * Chooses between an English and an Arabic value for the current language, falling back
   * through the other one before giving up on an empty string — so a record with a missing
   * translation still renders something rather than a blank line.
   */
  pick(en: string | null | undefined, ar: string | null | undefined): string {
    return (this.current() === 'ar' ? ar : en) ?? en ?? ar ?? '';
  }
}

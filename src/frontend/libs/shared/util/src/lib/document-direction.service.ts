import {
  Injectable,
  computed,
  effect,
  inject,
  makeEnvironmentProviders,
  provideAppInitializer,
  type EnvironmentProviders,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { LanguageService, type AppLanguage } from './language.service';

/** Writing direction of the document root. */
export type TextDirection = 'ltr' | 'rtl';

/** The one place the language -> direction mapping lives. Arabic is right-to-left; nothing else
 * this app supports is. Exported so tests and templates can ask without duplicating the rule. */
export function directionFor(language: AppLanguage): TextDirection {
  return language === 'ar' ? 'rtl' : 'ltr';
}

/**
 * Keeps `<html dir>` and `<html lang>` in step with {@link LanguageService}.
 *
 * Without this, picking Arabic changed which half of a bilingual API field rendered and nothing
 * else: Arabic text laid out left-to-right, the sidebar still on the left, numeric columns still
 * aligned to the physical right. The CSS was never the problem — `tokens.css` and `components.css`
 * were written against logical properties (`margin-inline`, `inset-inline-start`,
 * `text-align: start`) from the start, exactly so a `dir` flip would mirror the layout for free.
 * The single attribute that activates all of it was simply never being set.
 *
 * `lang` matters as much as `dir` and is easier to forget: it is what tells a screen reader to
 * switch pronunciation to Arabic, and what lets `:lang()` selectors and hyphenation work. A page
 * that flips direction while still claiming `lang="en"` is mirrored but not accessible.
 *
 * **This is direction switching, not internationalisation.** UI strings stay English in both
 * modes — this workspace has no message catalogue. See ADR-0010 for the full boundary.
 */
@Injectable({ providedIn: 'root' })
export class DocumentDirectionService {
  private readonly language = inject(LanguageService);
  private readonly document = inject(DOCUMENT);

  /** The direction the document is in right now. Read it in a template to re-render on change. */
  readonly direction = computed<TextDirection>(() => directionFor(this.language.current()));

  constructor() {
    // An `effect` rather than a `computed`, because writing to the DOM is the entire point — this
    // is one of the few places an effect is the correct tool rather than a `computed` in
    // disguise. Runs once on creation (setting the initial `dir`/`lang`, which also corrects a
    // hand-edited `index.html`) and again on every language change.
    effect(() => {
      this.apply(this.language.current());
    });
  }

  private apply(language: AppLanguage): void {
    const root = this.document.documentElement;
    // `documentElement` is absent in a bare/detached document; guard rather than throw from an
    // effect, where a throw would surface as an unrelated-looking global error.
    if (root === null || root === undefined) {
      return;
    }
    root.setAttribute('dir', directionFor(language));
    root.setAttribute('lang', language);
  }
}

/**
 * Installs {@link DocumentDirectionService} eagerly at bootstrap.
 *
 * The explicit initializer is load-bearing. The service is `providedIn: 'root'`, so it is only
 * constructed when something injects it — and nothing does: its whole job is a side effect on the
 * document. Without this, the service would exist, be fully tested, and never once run.
 */
export function provideDocumentDirection(): EnvironmentProviders {
  return makeEnvironmentProviders([
    provideAppInitializer(() => {
      inject(DocumentDirectionService);
    }),
  ]);
}

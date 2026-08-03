import { Component, ChangeDetectionStrategy, computed, inject, input, output, signal } from '@angular/core';
import { LanguageService } from '@aj-boilerplate/shared/util';
import type { FeatureAnnouncement } from '@aj-boilerplate/data-access/api-client';

interface BulletPart {
  kind: 'bullet';
  emoji?: string;
  title: string;
  description?: string;
}
interface ParagraphPart {
  kind: 'paragraph';
  text: string;
}
export type WhatsNewBodyPart = BulletPart | ParagraphPart;

/** Number of tints the benefit cards cycle through. Matches `.wn-bullet--tone-N` in the CSS. */
const BULLET_TONES = 5;

/**
 * "What's new" feature spotlight. Renders a carousel of unread feature announcements with a
 * gradient hero band, a bouncing spotlight glyph, and a stack of tinted benefit cards. Body
 * content uses a light markdown:
 *   - Lines starting with `"- "` become benefit cards. The leading emoji becomes the card icon;
 *     an optional `" — "` (em dash, spaced) separates the card title from its description.
 *   - Every other non-empty line becomes a centred paragraph.
 *   - Blank lines are structural separators and render nothing of their own.
 *
 * Backdrop click is intentionally inert — users must explicitly acknowledge via "Got it" or the X
 * so the server-side acknowledgement is only written on a deliberate dismiss. See `onBackdrop`.
 *
 * ── DELIBERATE, BOUNDED EXCEPTION TO THE "PrimeNG only" RULE ──────────────────────────────────
 * This workspace forbids bare `<button>`/`<input>` in templates and forbids per-component visual
 * values (see `CLAUDE.md` §3, `DESIGN.md` §6, and `libs/shared/ui/README.md`). This component
 * breaks both, on purpose and with sign-off: it is a bespoke, one-off marketing surface whose
 * gradients, confetti, and seven keyframe animations ARE the deliverable — there is no PrimeNG
 * component that renders it, and expressing it through `p-dialog` would mean fighting the theme
 * layer to produce something that must not look like the rest of the application anyway. The
 * exception is scoped to THIS component and does not generalise: everything else in `shared/ui`
 * stays PrimeNG-only and token-driven. Accessibility is not part of the exception — the dialog
 * role, `aria-modal`, `aria-labelledby`, the labelled close button, and the `role="tab"` dots are
 * all carried explicitly here precisely because PrimeNG is not supplying them.
 */
@Component({
  selector: 'app-whats-new-modal',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './whats-new-modal.html',
  styleUrl: './whats-new-modal.css',
})
export class WhatsNewModalComponent {
  private readonly lang = inject(LanguageService);

  readonly features = input.required<FeatureAnnouncement[]>();
  /** Emits every announcement id that was shown, so the caller acknowledges them in one request. */
  readonly closed = output<string[]>();

  readonly currentIndex = signal(0);

  readonly current = computed<FeatureAnnouncement | null>(() => {
    const list = this.features();
    if (list.length === 0) return null;
    const i = Math.min(this.currentIndex(), list.length - 1);
    return list[i] ?? null;
  });

  readonly total = computed(() => this.features().length);
  readonly isLast = computed(() => this.currentIndex() >= this.total() - 1);
  readonly isFirst = computed(() => this.currentIndex() <= 0);

  // Static bilingual chrome. The workspace has no message-catalogue library and this component is
  // not the place to introduce one, so the handful of fixed strings sit here and go through the
  // same `pick` helper as the server-supplied title and body.
  readonly eyebrowLabel = computed(() => this.lang.pick("What's new", 'الجديد'));
  readonly gotItLabel = computed(() => this.lang.pick('Got it', 'حسناً'));
  readonly nextLabel = computed(() => this.lang.pick('Next', 'التالي'));
  readonly previousLabel = computed(() => this.lang.pick('Previous', 'السابق'));
  readonly paginationLabel = computed(() => this.lang.pick('Page indicator', 'مؤشر الصفحة'));

  title(): string {
    const c = this.current();
    if (!c) return '';
    return this.lang.pick(c.titleEn, c.titleAr);
  }

  parts(): WhatsNewBodyPart[] {
    const body = (() => {
      const c = this.current();
      if (!c) return '';
      return this.lang.pick(c.bodyEn, c.bodyAr) ?? '';
    })().trim();
    if (!body) return [];

    const out: WhatsNewBodyPart[] = [];
    for (const raw of body.split(/\r?\n/)) {
      const line = raw.trim();
      if (!line) continue;
      if (line.startsWith('- ')) {
        const rest = line.slice(2).trim();
        const { emoji, text } = this.splitLeadingEmoji(rest);
        // First " — " (em-dash with spaces) splits title from description. Fallback: everything
        // is title.
        const dashIdx = text.indexOf(' — ');
        let title = text;
        let description: string | undefined;
        if (dashIdx >= 0) {
          title = text.slice(0, dashIdx).trim();
          description = text.slice(dashIdx + 3).trim();
        }
        out.push({ kind: 'bullet', emoji, title, description });
      } else {
        out.push({ kind: 'paragraph', text: line });
      }
    }
    return out;
  }

  /** Returns leading emoji sequence + the rest of the string. */
  private splitLeadingEmoji(s: string): { emoji?: string; text: string } {
    const m = /^(\p{Extended_Pictographic}(?:️)?[\s]*)+/u.exec(s);
    if (!m) return { text: s };
    const emoji = m[0].trim();
    const text = s.slice(m[0].length).trim();
    return { emoji, text };
  }

  /**
   * Background-tint class for the bullet icon — rotates through a small palette to give each row
   * visual identity (matches the iOS-style "feature list" pattern).
   */
  bulletTone(i: number): string {
    return `wn-bullet--tone-${i % BULLET_TONES}`;
  }

  next(): void {
    if (this.isLast()) {
      this.dismiss();
      return;
    }
    this.currentIndex.update((v) => v + 1);
  }

  prev(): void {
    if (this.isFirst()) return;
    this.currentIndex.update((v) => v - 1);
  }

  goTo(i: number): void {
    if (i < 0 || i >= this.total()) return;
    this.currentIndex.set(i);
  }

  /** Emits EVERY shown announcement id, not just the current page's — one POST acks the lot. */
  dismiss(): void {
    const ids = this.features().map((f) => f.id);
    this.closed.emit(ids);
  }

  /**
   * Backdrop is intentionally non-dismissive. A stray click outside the panel must not count as
   * "I have read this": the acknowledgement is permanent and per-user across every device, so it
   * is only written on an explicit "Got it" or X. Do not "fix" this into a close handler.
   */
  onBackdrop(): void {
    /* no-op — see the doc comment above. */
  }
}

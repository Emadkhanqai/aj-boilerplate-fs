import { render, screen, fireEvent } from '@testing-library/angular';
import { describe, expect, it, vi } from 'vitest';
import type { FeatureAnnouncement } from '@aj-boilerplate/data-access/api-client';
import { WhatsNewModalComponent } from './whats-new-modal';

function announcement(overrides: Partial<FeatureAnnouncement> = {}): FeatureAnnouncement {
  return {
    id: 'f-1',
    key: 'sample-v1',
    titleEn: 'Saved views are here',
    titleAr: null,
    bodyEn: 'A short lead-in line.',
    bodyAr: null,
    displayOrder: 0,
    createdAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

async function renderModal(features: FeatureAnnouncement[], closed = vi.fn()) {
  const view = await render(WhatsNewModalComponent, { inputs: { features }, on: { closed } });
  return { ...view, closed };
}

describe('WhatsNewModalComponent', () => {
  describe('accessibility', () => {
    it('exposes the panel as a labelled modal dialog', async () => {
      await renderModal([announcement()]);

      const dialog = screen.getByRole('dialog');
      expect(dialog.getAttribute('aria-modal')).toBe('true');
      expect(dialog.getAttribute('aria-labelledby')).toBe('wn-title');
      // The referenced element must actually exist, or the label resolves to nothing.
      expect(dialog.querySelector('#wn-title')?.textContent).toContain('Saved views are here');
    });

    it('labels the close button', async () => {
      await renderModal([announcement()]);

      expect(screen.getByRole('button', { name: 'Close' })).toBeTruthy();
    });

    it('makes the scrolling body reachable from the keyboard, with a name', async () => {
      // The body scrolls (`overflow-y: auto`). If it is not focusable, a keyboard-only
      // user cannot scroll it and simply never sees the rest of a long announcement —
      // axe's `scrollable-region-focusable`, WCAG 2.1.1. Regression test: dropping the
      // tabindex here is a silent accessibility loss that nothing else catches.
      const { container } = await renderModal([announcement()]);

      const body = container.querySelector('.wn-body');
      expect(body?.getAttribute('tabindex')).toBe('0');
      // Focusable and anonymous is its own defect, so the name has to resolve too.
      expect(body?.getAttribute('aria-labelledby')).toBe('wn-title');
      expect(container.querySelector('#wn-title')).toBeTruthy();
    });

    it('marks the pagination dots as tabs and reflects the selected one', async () => {
      await renderModal([announcement({ id: 'a' }), announcement({ id: 'b' })]);

      const tabs = screen.getAllByRole('tab');
      expect(tabs).toHaveLength(2);
      expect(tabs[0]?.getAttribute('aria-selected')).toBe('true');
      expect(tabs[1]?.getAttribute('aria-selected')).toBe('false');
      expect(screen.getByRole('tablist').getAttribute('aria-label')).toBe('Page indicator');
    });
  });

  describe('single announcement', () => {
    it('renders the title and the "Got it" call to action, with no carousel chrome', async () => {
      await renderModal([announcement()]);

      expect(screen.getByText('Saved views are here')).toBeTruthy();
      expect(screen.getByText('Got it')).toBeTruthy();
      expect(screen.queryByRole('tablist')).toBeNull();
      expect(screen.queryByText('Previous')).toBeNull();
      expect(screen.queryByText('1 / 1')).toBeNull();
    });

    it('emits the id when "Got it" is pressed', async () => {
      const { closed } = await renderModal([announcement({ id: 'only' })]);

      fireEvent.click(screen.getByText('Got it'));

      expect(closed).toHaveBeenCalledWith(['only']);
    });

    it('emits the id when the X button is pressed', async () => {
      const { closed } = await renderModal([announcement({ id: 'only' })]);

      fireEvent.click(screen.getByRole('button', { name: 'Close' }));

      expect(closed).toHaveBeenCalledWith(['only']);
    });
  });

  describe('carousel', () => {
    const three = [
      announcement({ id: 'a', titleEn: 'First' }),
      announcement({ id: 'b', titleEn: 'Second' }),
      announcement({ id: 'c', titleEn: 'Third' }),
    ];

    it('shows a counter and Next/Previous once there is more than one announcement', async () => {
      await renderModal(three);

      expect(screen.getByText('1 / 3')).toBeTruthy();
      expect(screen.getByText('Next')).toBeTruthy();
      expect(screen.getByText('Previous')).toBeTruthy();
    });

    it('advances through the pages with Next, ending on "Got it"', async () => {
      await renderModal(three);

      expect(screen.getByText('First')).toBeTruthy();

      fireEvent.click(screen.getByText('Next'));
      expect(screen.getByText('Second')).toBeTruthy();
      expect(screen.getByText('2 / 3')).toBeTruthy();

      fireEvent.click(screen.getByText('Next'));
      expect(screen.getByText('Third')).toBeTruthy();
      expect(screen.getByText('3 / 3')).toBeTruthy();
      // Last page swaps the CTA rather than adding a fourth page.
      expect(screen.queryByText('Next')).toBeNull();
      expect(screen.getByText('Got it')).toBeTruthy();
    });

    it('goes back with Previous', async () => {
      await renderModal(three);

      fireEvent.click(screen.getByText('Next'));
      fireEvent.click(screen.getByText('Previous'));

      expect(screen.getByText('First')).toBeTruthy();
      expect(screen.getByText('1 / 3')).toBeTruthy();
    });

    it('disables Previous on the first page and does not move backwards past it', async () => {
      await renderModal(three);

      const previous = screen.getByText('Previous').closest('button');
      expect(previous?.hasAttribute('disabled')).toBe(true);

      fireEvent.click(screen.getByText('Previous'));
      expect(screen.getByText('1 / 3')).toBeTruthy();
    });

    it('jumps straight to a page when its dot is clicked', async () => {
      await renderModal(three);

      const tabs = screen.getAllByRole('tab');
      fireEvent.click(tabs[2] as HTMLElement);

      expect(screen.getByText('Third')).toBeTruthy();
      expect(screen.getByText('3 / 3')).toBeTruthy();
      expect(screen.getAllByRole('tab')[2]?.getAttribute('aria-selected')).toBe('true');
    });

    it('acknowledges EVERY shown id in one emission, not just the last page', async () => {
      const { closed } = await renderModal(three);

      fireEvent.click(screen.getByText('Next'));
      fireEvent.click(screen.getByText('Next'));
      fireEvent.click(screen.getByText('Got it'));

      expect(closed).toHaveBeenCalledTimes(1);
      expect(closed).toHaveBeenCalledWith(['a', 'b', 'c']);
    });

    it('acknowledges every id even when dismissed from the first page via X', async () => {
      const { closed } = await renderModal(three);

      fireEvent.click(screen.getByRole('button', { name: 'Close' }));

      expect(closed).toHaveBeenCalledWith(['a', 'b', 'c']);
    });
  });

  describe('the backdrop is intentionally inert', () => {
    it('does NOT dismiss when the backdrop is clicked', async () => {
      const { closed, container } = await renderModal([announcement()]);

      const backdrop = container.querySelector('.wn-backdrop');
      expect(backdrop).not.toBeNull();
      fireEvent.click(backdrop as Element);

      expect(closed).not.toHaveBeenCalled();
      // …and the dialog is still on screen.
      expect(screen.getByRole('dialog')).toBeTruthy();
    });
  });

  describe('light-markdown body parser', () => {
    it('renders a "- emoji Title — description" line as a benefit card', async () => {
      const { container } = await renderModal([
        announcement({ bodyEn: '- 🔖 Saved views — keep any filter one click away' }),
      ]);

      const bullet = container.querySelector('.wn-bullet');
      expect(bullet).not.toBeNull();
      expect(bullet?.querySelector('.wn-bullet-icon')?.textContent?.trim()).toBe('🔖');
      expect(bullet?.querySelector('.wn-bullet-title')?.textContent).toBe('Saved views');
      expect(bullet?.querySelector('.wn-bullet-desc')?.textContent).toBe(
        'keep any filter one click away',
      );
    });

    it('treats the whole remainder as the title when there is no em-dash', async () => {
      const { container } = await renderModal([announcement({ bodyEn: '- ⚡ Faster search' })]);

      const bullet = container.querySelector('.wn-bullet');
      expect(bullet?.querySelector('.wn-bullet-icon')?.textContent?.trim()).toBe('⚡');
      expect(bullet?.querySelector('.wn-bullet-title')?.textContent).toBe('Faster search');
      expect(bullet?.querySelector('.wn-bullet-desc')).toBeNull();
    });

    it('falls back to a bullet glyph when a bullet line has no leading emoji', async () => {
      const { container } = await renderModal([announcement({ bodyEn: '- Plain benefit' })]);

      const bullet = container.querySelector('.wn-bullet');
      expect(bullet?.querySelector('.wn-bullet-icon')?.textContent?.trim()).toBe('•');
      expect(bullet?.querySelector('.wn-bullet-title')?.textContent).toBe('Plain benefit');
    });

    it('renders every other non-blank line as a paragraph', async () => {
      const { container } = await renderModal([
        announcement({ bodyEn: 'A lead-in sentence.\nAnd a second one.' }),
      ]);

      const paragraphs = container.querySelectorAll('.wn-paragraph');
      expect(paragraphs).toHaveLength(2);
      expect(paragraphs[0]?.textContent).toBe('A lead-in sentence.');
      expect(paragraphs[1]?.textContent).toBe('And a second one.');
      expect(container.querySelector('.wn-bullet')).toBeNull();
    });

    it('treats blank lines as separators that render nothing of their own', async () => {
      const { container } = await renderModal([
        announcement({ bodyEn: '\n\nA lead-in.\n\n\n- 🌙 Dark mode — follows your system\n\n' }),
      ]);

      expect(container.querySelectorAll('.wn-paragraph')).toHaveLength(1);
      expect(container.querySelectorAll('.wn-bullet')).toHaveLength(1);
    });

    it('mixes paragraphs and bullets in document order and cycles the bullet tints', async () => {
      const { container } = await renderModal([
        announcement({
          bodyEn: [
            'A lead-in.',
            '- 1️⃣ One',
            '- 2️⃣ Two',
            '- 3️⃣ Three',
            '- 4️⃣ Four',
            '- 5️⃣ Five',
            '- 6️⃣ Six',
          ].join('\n'),
        }),
      ]);

      expect(container.querySelectorAll('.wn-paragraph')).toHaveLength(1);
      const bullets = container.querySelectorAll('.wn-bullet');
      expect(bullets).toHaveLength(6);
      // Index 0 is the paragraph, so the bullets start at tone-1 and wrap after five tones.
      expect(bullets[0]?.classList.contains('wn-bullet--tone-1')).toBe(true);
      expect(bullets[4]?.classList.contains('wn-bullet--tone-0')).toBe(true);
      expect(bullets[5]?.classList.contains('wn-bullet--tone-1')).toBe(true);
    });

    it('renders nothing in the body for an empty or whitespace-only announcement body', async () => {
      const { container } = await renderModal([announcement({ bodyEn: '   \n  \n' })]);

      expect(container.querySelector('.wn-body')?.children).toHaveLength(0);
    });

    it('re-parses the body when the carousel moves to the next announcement', async () => {
      const { container } = await renderModal([
        announcement({ id: 'a', bodyEn: 'Just a paragraph.' }),
        announcement({ id: 'b', bodyEn: '- 🔔 Alerts — now on every tab' }),
      ]);

      expect(container.querySelector('.wn-bullet')).toBeNull();

      fireEvent.click(screen.getByText('Next'));

      expect(container.querySelector('.wn-bullet-title')?.textContent).toBe('Alerts');
    });
  });
});

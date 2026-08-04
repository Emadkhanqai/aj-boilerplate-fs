import { mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { ADMIN, signInAs } from '../fixtures/auth';
import { waitForAnimations } from '../fixtures/settle';
import { dismissWhatsNew } from '../fixtures/whats-new';

/**
 * Right-to-left layout, proven rather than asserted.
 *
 * `DESIGN.md` §5 has always required logical properties, so the CSS was ready for a mirrored
 * layout long before anything could trigger one — the `dir` attribute was simply never set. These
 * tests cover the whole chain: the toggle sets the language signal, `DocumentDirectionService`
 * writes `dir`/`lang` on the document root, and the browser mirrors the layout off the back of it.
 *
 * The geometry assertions are the point. Checking `dir="rtl"` alone would pass just as happily on
 * a page whose CSS ignored the attribute entirely; comparing where elements actually LAND is what
 * distinguishes a mirrored layout from an attribute nobody honoured.
 *
 * See ADR-0010 for what this does and — importantly — does not cover: UI strings are not
 * translated, because there is no i18n library here.
 */

// `__dirname` does not exist here: Playwright loads these specs as ES modules. Deriving it from
// `import.meta.url` is not a style preference — with `??` short-circuiting, a bare `__dirname`
// only throws when RTL_EVIDENCE_DIR is UNSET, so it runs clean locally with the variable exported
// and takes the whole spec file down in CI without it.
const HERE = dirname(fileURLToPath(import.meta.url));
const EVIDENCE_DIR = process.env['RTL_EVIDENCE_DIR'] ?? resolve(HERE, '../../../../dist/.playwright/rtl-evidence');

/** Bounding box of a selector, failing loudly rather than returning null. */
async function boxOf(page: Page, selector: string): Promise<{ x: number; width: number }> {
  const box = await page.locator(selector).first().boundingBox();
  if (box === null) {
    throw new Error(`No bounding box for ${selector} — it is not visible.`);
  }
  return { x: box.x, width: box.width };
}

async function switchToArabic(page: Page): Promise<void> {
  // The spotlight's inert backdrop would otherwise swallow this click.
  await dismissWhatsNew(page);
  await page.getByTestId('language-toggle').click();
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await waitForAnimations(page);
}

test.describe('Right-to-left direction', { tag: ['@mocked', '@rtl'] }, () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(EVIDENCE_DIR, { recursive: true });
    await signInAs(page, ADMIN);
  });

  test('starts left-to-right in English', async ({ page }) => {
    await page.goto('/items');
    await expect(page.getByTestId('item-row').first()).toBeVisible();

    await expect(page.locator('html')).toHaveAttribute('dir', 'ltr');
    await expect(page.locator('html')).toHaveAttribute('lang', 'en');
  });

  test('sets dir="rtl" AND lang="ar" together when Arabic is selected', async ({ page }) => {
    await page.goto('/items');
    await expect(page.getByTestId('item-row').first()).toBeVisible();

    await switchToArabic(page);

    // `lang` matters as much as `dir`: it is what switches screen-reader pronunciation. A page
    // that mirrors while still claiming lang="en" is an accessibility defect, not a cosmetic one.
    await expect(page.locator('html')).toHaveAttribute('lang', 'ar');
  });

  test('actually mirrors the layout — the sidebar moves to the right edge', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto('/items');
    await expect(page.getByTestId('item-row').first()).toBeVisible();
    await waitForAnimations(page);

    const ltrSidebar = await boxOf(page, '.sidebar');
    const viewport = page.viewportSize();
    expect(viewport).not.toBeNull();
    const width = viewport?.width ?? 1280;

    // In LTR the sidebar hugs the left edge.
    expect(ltrSidebar.x).toBeLessThan(width / 2);

    await switchToArabic(page);

    const rtlSidebar = await boxOf(page, '.sidebar');
    // In RTL it must be on the right. This is the assertion that would fail if the CSS were
    // written with physical `left`/`margin-left` instead of logical properties — the exact
    // regression the grep in this task was looking for.
    expect(rtlSidebar.x).toBeGreaterThan(width / 2);
  });

  test('captures desktop evidence in Arabic', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/items');
    await expect(page.getByTestId('item-row').first()).toBeVisible();
    await switchToArabic(page);

    await page.screenshot({ path: `${EVIDENCE_DIR}/rtl-desktop-1440.png`, fullPage: false });
  });

  test('captures 390px evidence in Arabic, and the page does not scroll sideways', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto('/items');
    await expect(page.getByTestId('item-row').first()).toBeVisible();
    await switchToArabic(page);

    await page.screenshot({ path: `${EVIDENCE_DIR}/rtl-mobile-390.png`, fullPage: false });

    // A mirrored layout that overflows its viewport is the classic RTL failure: something is
    // still pinned to a physical edge and pushes the document the other way.
    //
    // This reports WHICH elements cross the edge, not merely that something did. The bare
    // boolean this replaces was untraceable when it failed on CI and passed locally — and it
    // does differ by platform, because macOS overlay scrollbars leave `clientWidth` at the full
    // viewport width while a classic scrollbar on Linux takes layout width out of it. Naming
    // the offenders is what makes such a failure diagnosable from a CI log alone.
    const overflow = await page.evaluate(() => {
      const root = document.documentElement;
      const limit = root.clientWidth;
      const offenders: string[] = [];

      // Anything inside a fixed-position subtree is measured against the viewport and cannot
      // extend the scrollable area — the off-canvas drawer sits beyond the trailing edge by
      // design in RTL. Reporting its children buries the real cause under a dozen rows, which
      // is exactly what the first version of this diagnostic did.
      const inFixedSubtree = (el: Element): boolean => {
        for (let node: Element | null = el; node !== null; node = node.parentElement) {
          if (getComputedStyle(node).position === 'fixed') return true;
        }
        return false;
      };

      const found: { label: string; overhang: number }[] = [];
      for (const el of Array.from(document.querySelectorAll('*'))) {
        const rect = el.getBoundingClientRect();
        if (rect.width === 0 || rect.height === 0) continue;
        const overhang = Math.max(rect.right - limit, -rect.left);
        if (overhang <= 1) continue;
        if (inFixedSubtree(el)) continue;
        const cls = typeof el.className === 'string' ? el.className : '';
        found.push({
          label:
            `${el.tagName.toLowerCase()}${cls ? '.' + cls.trim().split(/\s+/).join('.') : ''} ` +
            `[left=${rect.left.toFixed(1)} right=${rect.right.toFixed(1)} over=${overhang.toFixed(1)}px]`,
          overhang,
        });
      }

      // Worst overhang first: the widest offender is the one to fix, and it is usually the
      // outermost element in a chain that all report the same overflow.
      found.sort((a, b) => b.overhang - a.overhang);
      offenders.push(...found.slice(0, 10).map((f) => f.label));

      return {
        scrollWidth: root.scrollWidth,
        clientWidth: limit,
        offenders,
      };
    });

    expect(
      overflow.scrollWidth,
      `the document scrolls horizontally at 390px in RTL — ` +
        `scrollWidth ${overflow.scrollWidth} vs clientWidth ${overflow.clientWidth}. ` +
        `Elements crossing the edge: ${overflow.offenders.join(' | ') || '(none in flow — check for a fixed or absolutely positioned element, or a scrollbar-width difference)'}`,
    ).toBeLessThanOrEqual(overflow.clientWidth + 1);
  });

  test('the item form mirrors too, and stays accessible in Arabic', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/items/new');
    await expect(page.getByTestId('item-name')).toBeVisible();
    await switchToArabic(page);

    await page.screenshot({ path: `${EVIDENCE_DIR}/rtl-form-1440.png`, fullPage: false });

    // Direction switching must not introduce accessibility violations — most obviously a
    // contrast or name/role/value regression from a mirrored control.
    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
    expect(results.violations, JSON.stringify(results.violations, null, 2)).toEqual([]);
  });
});

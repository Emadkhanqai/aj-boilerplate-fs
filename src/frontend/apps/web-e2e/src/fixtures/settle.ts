import type { Page } from '@playwright/test';

/**
 * Waits until every finite CSS animation on the page has finished.
 *
 * Why this exists: axe-core samples computed colours at the instant it runs. If it runs while an
 * entrance animation is still fading a panel in, it measures the half-transparent blend and
 * reports a wall of `color-contrast` violations that do not exist once the page has settled —
 * near-white text on near-white background, at 1.1:1. Those are phantoms, and "fix" attempts
 * chase colours that were never wrong.
 *
 * Infinite animations (spinners, pulsing rails) are excluded deliberately: awaiting `finished` on
 * one never resolves.
 */
export async function waitForAnimations(page: Page): Promise<void> {
  await page.evaluate(() =>
    Promise.all(
      document
        .getAnimations()
        .filter((animation) => animation.effect?.getComputedTiming().iterations !== Infinity)
        .map((animation) => animation.finished.catch(() => undefined)),
    ),
  );
}

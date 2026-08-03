import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { ADMIN, signInAs } from '../fixtures/auth';
import { waitForAnimations } from '../fixtures/settle';

/**
 * Settles the first authenticated route before scanning it.
 *
 * `waitForAnimations` alone is not enough here, and the reason is a race worth stating: the
 * "What's new" spotlight mounts only once the `unack` response lands, which is AFTER the page
 * heading becomes visible. So `waitForAnimations` would run while no modal existed yet, return
 * immediately, and axe would then sample the panel mid-fade — reporting `color-contrast` at
 * roughly 1.05:1 on text whose real palette is fine. Those are the phantom violations
 * `fixtures/settle.ts` describes, arriving through a door it cannot close on its own.
 *
 * Waiting for the panel first is deterministic, not a workaround: the demo MSW handler seeds
 * exactly one unacknowledged announcement per browser session, so the modal WILL appear. If it
 * ever stops appearing this fails loudly rather than quietly scanning less than it claims to.
 *
 * Note what this does NOT do: it does not dismiss the modal. The spotlight is real UI and stays
 * in the scan.
 */
async function settleAuthenticatedShell(page: Page): Promise<void> {
  await expect(page.locator('.wn-panel')).toBeVisible();
  await waitForAnimations(page);
}

/**
 * Accessibility gate — axe-core scans on the routes that matter most: the public login screen,
 * the authenticated shell, and a page with real form controls.
 *
 * No rules are disabled here, deliberately. If a scan ever finds a violation judged acceptable to
 * leave, document the rule id AND the reason in the repository's known-limitations notes rather
 * than silently excluding it from the scan — an exclusion nobody can find is the same as no test.
 *
 * Every scan waits for the page to settle first (see `waitForAnimations`): scanning mid-render
 * measures transient states that do not exist once the page has finished painting.
 *
 * Add a scan whenever you add a route with a new interaction pattern.
 */
test.describe('Accessibility — critical routes', { tag: ['@mocked', '@accessibility'] }, () => {
  test('login page has no detectable violations', async ({ page }) => {
    await page.goto('/login');
    await expect(page.getByRole('heading', { name: /choose a demo profile|sign in/i })).toBeVisible();
    await waitForAnimations(page);

    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();

    expect(results.violations, JSON.stringify(results.violations, null, 2)).toEqual([]);
  });

  test('home page has no detectable violations', async ({ page }) => {
    await signInAs(page, ADMIN);
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Home', level: 1 })).toBeVisible();
    await settleAuthenticatedShell(page);

    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();

    expect(results.violations, JSON.stringify(results.violations, null, 2)).toEqual([]);
  });

  test('item list has no detectable violations', async ({ page }) => {
    await signInAs(page, ADMIN);
    await page.goto('/items');
    // Wait for real rows: scanning while the table still shows its loading state covers a
    // different DOM than the one users read.
    await expect(page.getByTestId('item-row').first()).toBeVisible();
    await settleAuthenticatedShell(page);

    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();

    expect(results.violations, JSON.stringify(results.violations, null, 2)).toEqual([]);
  });

  test('item form has no detectable violations', async ({ page }) => {
    await signInAs(page, ADMIN);
    await page.goto('/items/new');
    await expect(page.getByTestId('item-name')).toBeVisible();
    await settleAuthenticatedShell(page);

    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();

    expect(results.violations, JSON.stringify(results.violations, null, 2)).toEqual([]);
  });
});

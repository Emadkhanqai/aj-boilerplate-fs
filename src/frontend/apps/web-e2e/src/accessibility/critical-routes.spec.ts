import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { ADMIN, signInAs } from '../fixtures/auth';
import { waitForAnimations } from '../fixtures/settle';

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
    await waitForAnimations(page);

    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();

    expect(results.violations, JSON.stringify(results.violations, null, 2)).toEqual([]);
  });

  test('item list has no detectable violations', async ({ page }) => {
    await signInAs(page, ADMIN);
    await page.goto('/items');
    // Wait for real rows: scanning while the table still shows its loading state covers a
    // different DOM than the one users read.
    await expect(page.getByTestId('item-row').first()).toBeVisible();
    await waitForAnimations(page);

    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();

    expect(results.violations, JSON.stringify(results.violations, null, 2)).toEqual([]);
  });

  test('item form has no detectable violations', async ({ page }) => {
    await signInAs(page, ADMIN);
    await page.goto('/items/new');
    await expect(page.getByTestId('item-name')).toBeVisible();
    await waitForAnimations(page);

    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();

    expect(results.violations, JSON.stringify(results.violations, null, 2)).toEqual([]);
  });
});

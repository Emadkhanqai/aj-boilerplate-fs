import { expect, test } from '@playwright/test';
import { ADMIN, signInAs } from '../fixtures/auth';
import { dismissWhatsNew } from '../fixtures/whats-new';

/**
 * The sample end-to-end journey: list -> create -> edit -> delete, against the MSW-backed `demo`
 * build (see `playwright.config.ts`), so it runs with no backend and no shared state.
 *
 * Written the way every journey here should be: role-based locators and `data-testid` hooks
 * rather than CSS selectors, one user-visible outcome asserted per step, and no `waitForTimeout`.
 *
 * SAMPLE — replace with your product's own critical journeys.
 */
test.describe('Items CRUD journey', { tag: ['@mocked'] }, () => {
  test.beforeEach(async ({ page }) => {
    await signInAs(page, ADMIN);
  });

  test('lists, creates, edits and deletes an item', async ({ page }) => {
    // --- list ---
    await page.goto('/items');
    await expect(page.getByRole('heading', { name: 'Items', level: 1 })).toBeVisible();
    await expect(page.getByTestId('item-row').first()).toBeVisible();
    // The demo build seeds one "What's new" announcement per session, and its deliberately inert
    // backdrop (ADR-0007) intercepts every click aimed at the page beneath it.
    await dismissWhatsNew(page);

    // --- create ---
    const name = `Playwright item ${Date.now()}`;
    await page.getByTestId('new-item').click();
    await expect(page.getByRole('heading', { name: 'New Item', level: 1 })).toBeVisible();
    await page.getByTestId('item-name').fill(name);
    await page.getByTestId('item-description').fill('Created by the sample journey.');
    await page.getByTestId('item-save').click();

    await expect(page).toHaveURL(/\/items$/);
    await page.getByTestId('item-search').fill(name);
    await expect(page.getByRole('link', { name })).toBeVisible();

    // --- edit ---
    await page.getByRole('link', { name }).click();
    await expect(page.getByRole('heading', { name: 'Edit Item', level: 1 })).toBeVisible();
    const renamed = `${name} (edited)`;
    await page.getByTestId('item-name').fill(renamed);
    await page.getByTestId('item-save').click();

    await expect(page).toHaveURL(/\/items$/);
    await page.getByTestId('item-search').fill(renamed);
    await expect(page.getByRole('link', { name: renamed })).toBeVisible();

    // --- delete ---
    await page.getByRole('button', { name: `Delete ${renamed}` }).click();
    await expect(page.getByRole('dialog')).toContainText('Delete this item?');
    await page.getByRole('button', { name: 'Delete', exact: true }).click();

    await expect(page.getByRole('link', { name: renamed })).toBeHidden();
  });

  test('search narrows the list', async ({ page }) => {
    await page.goto('/items');
    await expect(page.getByTestId('item-row').first()).toBeVisible();

    await page.getByTestId('item-search').fill('Sample item 01');

    await expect(page.getByTestId('item-row')).toHaveCount(1);
  });
});

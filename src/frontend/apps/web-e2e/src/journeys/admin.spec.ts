import { expect, test } from '@playwright/test';
import { ADMIN, VIEWER, signInAs } from '../fixtures/auth';
import { dismissWhatsNew } from '../fixtures/whats-new';

/**
 * Administration journeys against the MSW demo directory (`apps/web/src/mocks/admin-handlers.ts`):
 * users & roles, sign-in history, the portal switcher, and the capability gate.
 */
test.describe('Administration', { tag: ['@mocked'] }, () => {
  test('users: lists, searches, adds (domain refused then accepted), edits groups, disables', async ({ page }) => {
    await signInAs(page, ADMIN);
    await page.goto('/admin/users');
    await expect(page.getByRole('heading', { name: 'Users & roles', level: 1 })).toBeVisible();
    await expect(page.getByTestId('user-row').first()).toBeVisible();
    await dismissWhatsNew(page);

    // Rows-per-page default and the standard count text.
    await expect(page.locator('.p-paginator-current')).toHaveText('Showing 1 to 20 of 24');

    // search
    await page.getByTestId('user-search').fill('amal.abbas0');
    await expect(page.getByTestId('user-row')).toHaveCount(1);
    await page.getByTestId('user-search').fill('');
    await expect(page.getByTestId('user-row').first()).toBeVisible();

    // add: a disallowed domain is refused with a reason
    await page.getByTestId('add-user').getByRole('button').click();
    await page.getByTestId('cu-email').fill('someone@elsewhere.com');
    await page.getByTestId('cu-group').click();
    await page.getByRole('option', { name: 'Editors' }).click();
    await page.getByTestId('cu-submit').getByRole('button').click();
    await expect(page.getByTestId('create-error')).toContainText('email domain is not allowed');
    // ... then an allowed one is added and appears in the list
    await page.getByTestId('cu-email').fill('new.person@example.org');
    await page.getByTestId('cu-submit').getByRole('button').click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await page.getByTestId('user-search').fill('new.person');
    await expect(page.getByTestId('user-row')).toHaveCount(1);

    // groups: save and read what changed
    await page.getByRole('button', { name: /^Edit groups for/ }).click();
    await page.getByTestId('ug-groups').click();
    await page.getByRole('option', { name: 'Viewers' }).click();
    await page.keyboard.press('Escape');
    await page.getByTestId('ug-save').getByRole('button').click();
    await expect(page.getByTestId('groups-result')).toContainText('Added: Viewers');
    await page.getByRole('dialog').getByRole('button', { name: 'Close' }).click();

    // disable, confirmed
    await page.getByRole('button', { name: /^Disable / }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Disable', exact: true }).click();
    await expect(page.getByTestId('user-row').first()).toContainText('Disabled');
  });

  test('sign-in history: sorts by a column, filters by user, shows open sessions as Active', async ({ page }) => {
    await signInAs(page, ADMIN);
    await page.goto('/admin/sign-in-history');
    await expect(page.getByRole('heading', { name: 'Sign-in history', level: 1 })).toBeVisible();
    await expect(page.getByTestId('history-row').first()).toBeVisible();
    await dismissWhatsNew(page);

    await expect(page.locator('.p-paginator-current')).toHaveText('Showing 1 to 20 of 57');
    await expect(page.getByTestId('history-row').first()).toContainText('Active'); // newest session is open in the demo data

    // sort by Duration ascending: the shortest sessions come first, so no row shows Active first
    await page.getByRole('columnheader', { name: /^Duration/ }).click();
    await expect(page.getByRole('columnheader', { name: /^Duration/ })).toHaveAttribute('aria-sort', 'ascending');

    // filter to one person
    await page.getByTestId('history-user').click();
    await page.getByRole('option', { name: 'Amal Abbas' }).first().click();
    await expect(page.locator('.p-paginator-current')).toContainText('of 3');
    for (const row of await page.getByTestId('history-row').all()) await expect(row).toContainText('Amal Abbas');
  });

  test('the top bar offers a portal switcher with every portal the person may open', async ({ page }) => {
    await signInAs(page, ADMIN);
    await page.goto('/');
    await dismissWhatsNew(page);
    await page.getByTestId('portal-switcher').click();
    await expect(page.getByRole('menuitem')).toHaveText(['CAD', 'E-Budgeting', 'Unified portal']);
  });

  test('a viewer does not see the admin entries and is refused the routes', async ({ page }) => {
    await signInAs(page, VIEWER);
    await page.goto('/');
    await expect(page.getByRole('link', { name: 'Users & roles' })).toHaveCount(0);
    await page.goto('/admin/users');
    await expect(page).toHaveURL(/\/access-denied/);
  });
});

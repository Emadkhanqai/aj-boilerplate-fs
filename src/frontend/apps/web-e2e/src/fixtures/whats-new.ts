import { expect, type Page } from '@playwright/test';

/**
 * Dismisses the "What's new" spotlight before a journey interacts with the page beneath it.
 *
 * Why this is needed: the demo build's MSW handler seeds exactly one unacknowledged announcement
 * per browser session (`apps/web/src/mocks/handlers.ts`), and `AppLayoutComponent` asks for
 * unacknowledged announcements on every `NavigationEnd`. So the first authenticated route of any
 * test opens a modal over the page — and because its backdrop is deliberately inert (ADR-0007),
 * it swallows every click aimed at the UI underneath. Playwright reports that as
 * `<div class="wn-hero"> … intercepts pointer events` on an otherwise valid click.
 *
 * Call it AFTER the first `goto` of the authenticated shell, before interacting.
 *
 * **This waits rather than checking-and-shrugging, deliberately.** The modal only exists once the
 * `unack` response lands, so a fixture that looks for a close button and returns quietly when it
 * finds none passes or fails depending on whether the response beat it — the recipe for a test
 * that fails one run in five for reasons nobody can reproduce. The demo seed guarantees the modal
 * WILL appear, so waiting for it is deterministic, and if it ever stops appearing this fails
 * loudly instead of silently degrading into a no-op.
 *
 * **Not intercepting the network either.** `page.route` on the `unack` endpoint looks tidier, but
 * this app serves the demo API from an MSW *service worker*; layering Playwright's interception on
 * top of it made the whole suite flakier, including tests that never touch this modal. Measured,
 * not assumed — three consecutive runs, different failures each time.
 *
 * Deliberately NOT used by `src/accessibility/critical-routes.spec.ts`: the spotlight is real UI
 * and belongs in an accessibility scan. Hiding it there would suppress findings rather than fix
 * them — see that file's own note about never silently excluding things from the scan.
 */
export async function dismissWhatsNew(page: Page): Promise<void> {
  const closeButton = page.getByRole('button', { name: 'Close' });
  await closeButton.waitFor({ state: 'visible' });
  await closeButton.click();
  await expect(page.locator('.wn-panel')).toHaveCount(0);
}

import { fileURLToPath } from 'node:url';
import { defineConfig, devices } from '@playwright/test';
import { nxE2EPreset } from '@nx/playwright/preset';
import { workspaceRoot } from '@nx/devkit';

const currentFile = fileURLToPath(import.meta.url);

/**
 * Runs against the `demo` build/serve configuration (`apps/web/project.json`), which swaps in
 * `environment.demo.ts` so the app starts its MSW worker before first render. That is what makes
 * this suite self-contained and deterministic: no backend, no shared database, no test that fails
 * because someone else's data changed.
 *
 * `reuseExistingServer` picks up a dev server you already have running locally; CI always boots a
 * fresh one. Set `E2E_BASE_URL` to point the suite at a deployed environment instead.
 */
const baseURL = process.env['E2E_BASE_URL'] ?? 'http://localhost:4200';

export default defineConfig({
  ...nxE2EPreset(currentFile, { testDir: './src' }),
  outputDir: '../../dist/.playwright/apps/web-e2e/test-output',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 2 : 0,
  reporter: [
    ['html', { outputFolder: '../../dist/.playwright/apps/web-e2e/report', open: 'never' }],
    ['list'],
  ],
  use: {
    baseURL,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    // Pinned for determinism: locale and timezone affect any formatted date captured in an
    // assertion or a screenshot, and deviceScaleFactor affects pixel comparison on high-DPI hosts.
    // Entrance animations are handled per-test by `waitForAnimations` (src/fixtures/settle.ts) —
    // scanning or screenshotting mid-fade produces findings that do not survive the next frame.
    locale: 'en-US',
    timezoneId: 'UTC',
    deviceScaleFactor: 1,
  },
  expect: {
    toHaveScreenshot: { animations: 'disabled', maxDiffPixelRatio: 0.02 },
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  webServer: process.env['E2E_BASE_URL']
    ? undefined
    : {
        command: 'npx nx run web:serve:demo',
        url: baseURL,
        reuseExistingServer: !process.env['CI'],
        cwd: workspaceRoot,
        timeout: 120_000,
      },
});

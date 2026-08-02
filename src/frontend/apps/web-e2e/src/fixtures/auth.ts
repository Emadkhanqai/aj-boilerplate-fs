import type { Page } from '@playwright/test';

/** Mirrors `DEV_SESSION_STORAGE_KEY` in `libs/auth/src/lib/providers/dev-provider.ts`. */
export const DEV_SESSION_STORAGE_KEY = 'app.session.v1';

export interface DevSessionUser {
  userId: string;
  name: string;
  email: string;
  role: string;
}

/** Mirrors the `DEV_USERS` entries in `libs/auth/src/lib/providers/dev-provider.ts`. */
export const ADMIN: DevSessionUser = {
  userId: 'u-admin',
  name: 'Avery Admin',
  email: 'avery.admin@example.com',
  role: 'admin',
};

export const VIEWER: DevSessionUser = {
  userId: 'u-viewer',
  name: 'Vera Viewer',
  email: 'vera.viewer@example.com',
  role: 'viewer',
};

/**
 * Seeds a dev session into `localStorage` before the app's first script runs, skipping the login
 * UI for journeys that aren't testing login itself. Must be called before any `page.goto`.
 */
export async function signInAs(page: Page, user: DevSessionUser): Promise<void> {
  const session = {
    user: { userId: user.userId, name: user.name, email: user.email },
    roles: [user.role],
    accessToken: `dev.${user.userId}`,
  };
  await page.addInitScript(
    ({ key, value }: { key: string; value: string }) => window.localStorage.setItem(key, value),
    { key: DEV_SESSION_STORAGE_KEY, value: JSON.stringify(session) },
  );
}

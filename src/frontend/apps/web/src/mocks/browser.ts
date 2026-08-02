import { setupWorker } from 'msw/browser';
import { http, HttpResponse, delay as mswDelay } from 'msw';
import { handlers } from './handlers';

/**
 * Service-worker-backed API mock (MSW). Started ONLY from the `environment.apiMock`-guarded
 * dynamic import in `main.ts` (see `apps/web/src/main.ts`), so neither this module nor `msw`
 * is included in a `production`-configuration bundle. `onUnhandledRequest: 'bypass'` lets
 * Angular's own asset/HMR requests through untouched; only `/api/*` routes declared in
 * `handlers` are intercepted.
 */
const worker = setupWorker(...handlers);

type HttpMethod = 'get' | 'post' | 'put' | 'delete' | 'patch';

export interface E2EOverrideDescriptor {
  method: HttpMethod;
  /** MSW path pattern, e.g. `/api/v1/items` or `/api/v1/items/:id`. */
  path: string;
  status: number;
  body?: unknown;
  delayMs?: number;
}

/**
 * Installs a one-off runtime handler override, taking priority over the base `handlers` for any
 * matching request until `resetE2EOverrides()` is called. Exists SOLELY so Playwright E2E tests
 * (`apps/web-e2e`) can force deterministic HTTP failure/slow-response scenarios — Playwright's
 * own `page.route()`/`context.route()` cannot intercept requests MSW's Service Worker already
 * answers (verified empirically: the interception handler never fires for SW-served responses),
 * so this is the standard MSW-recommended alternative. Never called from application code.
 */
function installE2EOverride(descriptor: E2EOverrideDescriptor): void {
  worker.use(
    http[descriptor.method](descriptor.path, async () => {
      if (descriptor.delayMs !== undefined) {
        await mswDelay(descriptor.delayMs);
      }
      return HttpResponse.json(descriptor.body ?? { error: 'e2e-forced' }, { status: descriptor.status });
    }),
  );
}

function resetE2EOverrides(): void {
  worker.resetHandlers();
}

export async function startMockWorker(): Promise<void> {
  await worker.start({ onUnhandledRequest: 'bypass' });
  // Demo-only test hook — this whole module is tree-shaken out of `production` builds (see
  // `environment.demo.ts`/`environment.ts`), so `__mswE2E` never exists outside the demo build
  // Playwright runs against.
  (window as unknown as { __mswE2E?: { installOverride: typeof installE2EOverride; resetOverrides: typeof resetE2EOverrides } }).__mswE2E = {
    installOverride: installE2EOverride,
    resetOverrides: resetE2EOverrides,
  };
}

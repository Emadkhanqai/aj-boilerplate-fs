/**
 * Default (production/development) environment. This file is swapped for `environment.demo.ts`
 * via the `demo` build configuration's `fileReplacements` (see `apps/web/project.json`) — the
 * `@angular/build:application` executor in this workspace's Angular/Nx version has no built-in
 * `NG_APP_*`/`process.env` build-time env substitution for browser bundles, so `fileReplacements`
 * is the mechanism actually used here.
 *
 * `maybeStartMockWorker` is a no-op here, deliberately. `main.ts` calls it unconditionally
 * rather than branching on a boolean at runtime — a runtime `if (flag) { await import(...) }`
 * does NOT remove the dynamic import's target module graph from the bundle (verified: with a
 * boolean-guarded dynamic import, the MSW chunk showed up in the `production` build too, just
 * unexecuted). Swapping the *function itself* via `fileReplacements` means the `production`
 * build's module graph never references `../mocks/browser` at all, so the whole MSW dependency
 * graph is genuinely absent from the bundle — see `environment.demo.ts` for the variant that
 * does import it.
 */
export const environment = {
  apiMock: false,
  async maybeStartMockWorker(): Promise<void> {
    // Intentionally empty — production/development boot straight into `bootstrapApplication`.
  },
};

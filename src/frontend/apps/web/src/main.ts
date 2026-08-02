import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { App } from './app/app';
import { environment } from './environments/environment';
import { installChunkLoadErrorReload } from './chunk-load-error-handler';

// Must run before bootstrap: a lazy route's `import()` can fail as soon as the user navigates,
// which can happen before any Angular-level error handling is wired up. See
// `chunk-load-error-handler.ts` for what this detects and why (stale tab open across a deploy →
// old chunk hash no longer exists on the server → full reload re-fetches the current
// `index.html`/chunk hashes).
installChunkLoadErrorReload();

/**
 * Boot the app. `environment.maybeStartMockWorker()` starts the MSW worker BEFORE the first
 * render in the offline demo build only, so no request escapes to a nonexistent backend —
 * the gate is a FILE swap (Angular
 * `fileReplacements`, in the `demo` build configuration — see `apps/web/project.json`), not a
 * runtime boolean check: this workspace's `@angular/build:application` executor has no built-in
 * build-time substitution for `process.env`/`NG_APP_*` in the browser bundle (so a plain
 * `process.env` read would throw at runtime, `process` is undefined in the browser), AND a
 * runtime `if (flag) { await import(...) }` does NOT remove the imported module graph from a
 * `production` bundle (verified: the MSW chunk showed up in `production` output too, just
 * unexecuted). Swapping the whole `environment.ts`/`environment.demo.ts` module is what actually
 * keeps `./mocks/browser` and `msw` out of the `production` build's module graph.
 */
async function bootstrap(): Promise<void> {
  await environment.maybeStartMockWorker();
  await bootstrapApplication(App, appConfig);
}

void bootstrap().catch((err: unknown) => console.error(err));

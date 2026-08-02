/**
 * Offline demo environment — swapped in for `environment.ts` by the `demo` build configuration's
 * `fileReplacements`. `maybeStartMockWorker` dynamically imports the MSW worker and starts it
 * before `main.ts` bootstraps the app, so no request can escape to a real (nonexistent, in this
 * build) backend. Because this import only exists in THIS file, and `production` builds never
 * include this file, the MSW dependency graph never enters the `production` bundle's module
 * graph in the first place (see `environment.ts` for the no-op counterpart).
 */
export const environment = {
  apiMock: true,
  async maybeStartMockWorker(): Promise<void> {
    const { startMockWorker } = await import('../mocks/browser');
    await startMockWorker();
  },
};

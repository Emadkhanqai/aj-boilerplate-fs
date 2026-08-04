import { defineConfig } from 'vitest/config';
import { nxViteTsPaths } from '@nx/vite/plugins/nx-tsconfig-paths.plugin';
import { nxCopyAssetsPlugin } from '@nx/vite/plugins/nx-copy-assets.plugin';

export default defineConfig(() => ({
  // `import.meta.dirname`, not `__dirname`: this config is an ES module (.mts), where
  // `__dirname` is not defined. Vite tolerated it once; it is a hard error now.
  root: import.meta.dirname,
  cacheDir: '../../../node_modules/.vite/libs/shared/util',
  plugins: [nxViteTsPaths(), nxCopyAssetsPlugin(['*.md'])],
  test: {
    name: 'util',
    watch: false,
    globals: true,
    environment: 'jsdom',
    setupFiles: ['src/test-setup.ts'],
    include: ['{src,tests}/**/*.{test,spec}.{js,mjs,cjs,ts,mts,cts,jsx,tsx}'],
    reporters: ['default'],
    coverage: {
      reportsDirectory: '../../../coverage/libs/shared/util',
      provider: 'v8' as const,
      // lcov for SonarQube, text-summary for the CI log. Set HERE and not on the
      // command line: these two projects use the @nx/vite executor, whose `coverage`
      // option is an object, while every Angular project uses an executor whose
      // `coverage` is a boolean with a separate `coverageReporters` array. One shared
      // CLI flag cannot satisfy both — it crashes whichever half it was not written for.
      reporter: ['lcov', 'text-summary'],
    },
  },
}));

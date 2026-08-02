import { defineConfig } from 'vitest/config';
import { nxViteTsPaths } from '@nx/vite/plugins/nx-tsconfig-paths.plugin';
import { nxCopyAssetsPlugin } from '@nx/vite/plugins/nx-copy-assets.plugin';

export default defineConfig(() => ({
  root: __dirname,
  cacheDir: '../../../node_modules/.vite/libs/data-access/api-types',
  plugins: [nxViteTsPaths(), nxCopyAssetsPlugin(['*.md'])],
  test: {
    name: 'api-types',
    watch: false,
    globals: true,
    environment: 'node',
    include: ['{src,tests}/**/*.{test,spec}.{js,mjs,cjs,ts,mts,cts,jsx,tsx}'],
    // This lib is generated type declarations (see src/lib/types.ts) — there is no runtime
    // behavior to unit test. Allow the target to pass with zero spec files instead of
    // authoring a vacuous test just to satisfy the runner.
    passWithNoTests: true,
    reporters: ['default'],
    coverage: {
      reportsDirectory: '../../../coverage/libs/data-access/api-types',
      provider: 'v8' as const,
    },
  },
}));

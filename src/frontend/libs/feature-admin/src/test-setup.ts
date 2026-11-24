import '@testing-library/jest-dom/vitest';

// jsdom does not implement `window.matchMedia`. PrimeNG's Overlay service (used by p-select's
// dropdown and p-dialog) reads it to decide modal/breakpoint behavior, so any test that opens a
// PrimeNG overlay crashes without this polyfill. This is test-environment infrastructure, not a
// workaround for component behavior.
const noop = (): void => {
  /* no-op stub for a jsdom MediaQueryList event API no test in this lib needs to observe. */
};

if (typeof window !== 'undefined' && window.matchMedia === undefined) {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: (query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: noop,
      removeListener: noop,
      addEventListener: noop,
      removeEventListener: noop,
      dispatchEvent: () => false,
    }),
  });
}

// jsdom also does not implement `Element.prototype.scrollIntoView`. PrimeNG's `p-select` calls it
// on the already-selected `<li>` when its overlay opens with a non-empty `[ngModel]` —
// `LineRowEditorComponent`'s cost-format `p-select` (moved into this lib in this task) is the
// first component here to open a `p-select` overlay pre-seeded with a non-null value.
if (typeof Element !== 'undefined' && Element.prototype.scrollIntoView === undefined) {
  Element.prototype.scrollIntoView = noop;
}

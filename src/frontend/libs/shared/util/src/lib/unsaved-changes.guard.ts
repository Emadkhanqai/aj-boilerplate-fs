import type { CanDeactivateFn } from '@angular/router';

/**
 * Implemented by any routed component that can hold unsaved edits.
 *
 * The split between the two methods is the whole design. `hasUnsavedChanges` is a cheap, pure
 * question the guard asks on EVERY attempted navigation. `confirmDiscard` is only reached when
 * the answer is yes, and it is the COMPONENT's job — not the guard's — because the component is
 * the only thing that can render a dialog into its own template.
 *
 * That is what keeps `window.confirm()` out of this file. The obvious implementation of a
 * dirty-form guard is one line of `return window.confirm('Discard changes?')`, and it is wrong on
 * every axis this codebase cares about: it cannot be styled, it blocks the event loop, it names
 * neither the record nor the consequence, it is unthemeable in a right-to-left layout, and
 * `DESIGN.md` §6 bans it outright. Pushing the confirmation back to the component costs an
 * interface and buys the app's own `app-confirm-dialog`.
 */
export interface UnsavedChangesAware {
  /** True when leaving now would lose the user's work. Must not have side effects. */
  hasUnsavedChanges(): boolean;
  /**
   * Ask the user whether to discard. Resolves `true` to leave, `false` to stay. Implementations
   * render the app's own confirmation dialog and resolve when the user answers.
   */
  confirmDiscard(): Promise<boolean>;
}

/**
 * `CanDeactivate` guard that stops a navigation away from a form with unsaved edits until the
 * user confirms.
 *
 * Attach it to any route whose component implements {@link UnsavedChangesAware}:
 *
 * ```ts
 * {
 *   path: 'items/:id',
 *   loadComponent: () => import('@aj-boilerplate/feature-items').then((m) => m.ItemFormPageComponent),
 *   canDeactivate: [unsavedChangesGuard],
 * }
 * ```
 *
 * Covers in-app navigation only — router navigations, which is what a `CanDeactivate` guard is.
 * It does NOT cover closing the tab or a hard reload; only `beforeunload` can, and the browser
 * replaces that message with its own generic text and forbids a custom dialog entirely. A
 * `beforeunload` handler is a reasonable addition for a consuming project, but it is a different
 * mechanism with a different (much worse) UX, not an extension of this one.
 */
export const unsavedChangesGuard: CanDeactivateFn<UnsavedChangesAware> = (component) => {
  // A pristine form must never prompt. A guard that asks "are you sure?" when there is nothing to
  // lose trains users to dismiss it without reading, which is exactly how the one that mattered
  // gets dismissed too.
  if (!component.hasUnsavedChanges()) {
    return true;
  }
  return component.confirmDiscard();
};

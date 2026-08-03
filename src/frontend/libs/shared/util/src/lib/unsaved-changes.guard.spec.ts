import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import type { ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';
import { unsavedChangesGuard, type UnsavedChangesAware } from './unsaved-changes.guard';

/** The guard's three route arguments are irrelevant to it — it decides purely from the component. */
function runGuard(component: UnsavedChangesAware): boolean | Promise<boolean> {
  return TestBed.runInInjectionContext(() =>
    unsavedChangesGuard(
      component,
      {} as ActivatedRouteSnapshot,
      {} as RouterStateSnapshot,
      {} as RouterStateSnapshot,
    ),
  ) as boolean | Promise<boolean>;
}

describe('unsavedChangesGuard', () => {
  it('leaves immediately when the form is clean, without asking', async () => {
    const confirmDiscard = vi.fn(() => Promise.resolve(true));
    const result = runGuard({ hasUnsavedChanges: () => false, confirmDiscard });

    expect(result).toBe(true);
    // Not merely "it navigated" — it must not have prompted. A guard that asks on a pristine
    // form teaches users to dismiss the dialog reflexively.
    expect(confirmDiscard).not.toHaveBeenCalled();
  });

  it('asks the component to confirm when the form is dirty', async () => {
    const confirmDiscard = vi.fn(() => Promise.resolve(true));

    await expect(runGuard({ hasUnsavedChanges: () => true, confirmDiscard })).resolves.toBe(true);
    expect(confirmDiscard).toHaveBeenCalledOnce();
  });

  it('blocks the navigation when the user declines', async () => {
    const result = runGuard({
      hasUnsavedChanges: () => true,
      confirmDiscard: () => Promise.resolve(false),
    });

    await expect(result).resolves.toBe(false);
  });

  it('never calls window.confirm', async () => {
    const nativeConfirm = vi.spyOn(window, 'confirm').mockReturnValue(true);

    await runGuard({ hasUnsavedChanges: () => true, confirmDiscard: () => Promise.resolve(true) });

    // DESIGN.md §6 bans the native dialog. Asserting it directly means a future "simplification"
    // back to `window.confirm()` fails a test rather than passing review.
    expect(nativeConfirm).not.toHaveBeenCalled();
    nativeConfirm.mockRestore();
  });
});

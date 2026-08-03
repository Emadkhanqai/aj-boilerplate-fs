import { render, screen, fireEvent, within } from '@testing-library/angular';
import { provideRouter } from '@angular/router';
import { provideLocationMocks } from '@angular/common/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { QueryClient, provideTanStackQuery } from '@tanstack/angular-query-experimental';
import { MessageService } from 'primeng/api';
import { providePrimeNG } from 'primeng/config';
import { describe, expect, it, vi } from 'vitest';
import { of, throwError } from 'rxjs';
import { ApiError, ItemsApiService } from '@aj-boilerplate/data-access/api-client';
import type { ItemResponse } from '@aj-boilerplate/data-access/api-types';
import { ItemFormPageComponent } from './item-form-page';

const SAVED: ItemResponse = {
  id: '9',
  name: 'New thing',
  description: null,
  status: 'Draft',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: null,
  rowVersion: 'v1',
};

async function renderForm(api: Partial<ItemsApiService>) {
  return render(ItemFormPageComponent, {
    providers: [
      provideZonelessChangeDetection(),
      // A stub target for the post-save/row navigations, so they resolve instead of throwing.
      provideRouter([{ path: 'items', children: [] }]),
      provideLocationMocks(),
      provideTanStackQuery(new QueryClient({ defaultOptions: { queries: { retry: false } } })),
      providePrimeNG({}),
      MessageService,
      { provide: ItemsApiService, useValue: api },
    ],
  });
}

describe('ItemFormPageComponent (create mode)', () => {
  it('renders the create heading when there is no id in the route', async () => {
    await renderForm({});

    expect(screen.getByRole('heading', { name: 'New item' })).toBeTruthy();
  });

  it('refuses to submit an empty name and says why, inline', async () => {
    const create = vi.fn(() => of(SAVED));
    await renderForm({ create });

    fireEvent.click(screen.getByRole('button', { name: 'Create item' }));

    expect(await screen.findByText('Name is required.')).toBeTruthy();
    expect(create).not.toHaveBeenCalled();
  });

  it('sends a trimmed name and a null description when the description is blank', async () => {
    const create = vi.fn(() => of(SAVED));
    await renderForm({ create });

    fireEvent.input(screen.getByLabelText(/Name/), { target: { value: '  New thing  ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create item' }));

    await vi.waitFor(() =>
      expect(create).toHaveBeenCalledWith({ name: 'New thing', description: null, status: 'Draft' }),
    );
  });

  it('surfaces a save failure as a visible message', async () => {
    await renderForm({
      create: () => throwError(() => new ApiError(500, 'Server exploded', null, null)),
    });

    fireEvent.input(screen.getByLabelText(/Name/), { target: { value: 'Thing' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create item' }));

    expect(await screen.findByText(/Could not save this item/)).toBeTruthy();
  });

  it('shows the concurrency conflict banner — not a generic error — on a 409', async () => {
    await renderForm({
      create: () => throwError(() => new ApiError(409, 'Conflict', null, 'CONFLICT')),
    });

    fireEvent.input(screen.getByLabelText(/Name/), { target: { value: 'Thing' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create item' }));

    const banner = await screen.findByTestId('conflict-banner');
    expect(banner.textContent).toContain('Someone else changed this item');
    expect(screen.getByTestId('conflict-reload')).toBeTruthy();
  });
});

/**
 * The unsaved-changes guard, exercised through the real component rather than a stub — the guard
 * itself is unit-tested in `libs/shared/util/src/lib/unsaved-changes.guard.spec.ts`, and these
 * assert the half that lives HERE: that a dirty form reports itself dirty, and that confirming
 * raises the app's own dialog rather than a native one.
 */
describe('ItemFormPageComponent (unsaved changes)', () => {
  it('reports no unsaved changes on an untouched form', async () => {
    const { fixture } = await renderForm({});
    const component = fixture.componentInstance;

    expect(component.hasUnsavedChanges()).toBe(false);
  });

  it('reports unsaved changes once the user has typed', async () => {
    const { fixture } = await renderForm({});
    const component = fixture.componentInstance;

    fireEvent.input(screen.getByLabelText(/Name/), { target: { value: 'Half-typed' } });

    expect(component.hasUnsavedChanges()).toBe(true);
  });

  it('raises the app confirm dialog — not window.confirm — when the guard asks to discard', async () => {
    const nativeConfirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
    const { fixture } = await renderForm({});
    const component = fixture.componentInstance;

    fireEvent.input(screen.getByLabelText(/Name/), { target: { value: 'Half-typed' } });
    void component.confirmDiscard();
    fixture.detectChanges();

    expect(await screen.findByText('Discard your changes?')).toBeTruthy();
    expect(nativeConfirm).not.toHaveBeenCalled();
    nativeConfirm.mockRestore();
  });

  it('resolves true and closes the dialog when the user confirms the discard', async () => {
    const { fixture } = await renderForm({});
    const component = fixture.componentInstance;

    fireEvent.input(screen.getByLabelText(/Name/), { target: { value: 'Half-typed' } });
    const decision = component.confirmDiscard();
    fixture.detectChanges();

    fireEvent.click(await screen.findByRole('button', { name: 'Discard changes' }));

    await expect(decision).resolves.toBe(true);
  });

  it('resolves false — keeping the user on the page with their work — when they cancel', async () => {
    const { fixture } = await renderForm({});
    const component = fixture.componentInstance;

    fireEvent.input(screen.getByLabelText(/Name/), { target: { value: 'Half-typed' } });
    const decision = component.confirmDiscard();
    fixture.detectChanges();

    // Scoped to the dialog on purpose: the FORM also has a Cancel button, and an unscoped
    // query matches both. Clicking the wrong one would test nothing and look like it passed.
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    await expect(decision).resolves.toBe(false);
    // The half-typed value must still be there — a "stay" that silently cleared the form would
    // be worse than the navigation it prevented.
    expect((screen.getByLabelText(/Name/) as HTMLInputElement).value).toBe('Half-typed');
  });

  it('does not prompt after a successful save, because saving marks the form pristine', async () => {
    const { fixture } = await renderForm({ create: () => of(SAVED) });
    const component = fixture.componentInstance;

    fireEvent.input(screen.getByLabelText(/Name/), { target: { value: 'New thing' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create item' }));

    // Otherwise every successful save would ask "discard your changes?" on the way to the list —
    // the fastest way to teach users that this dialog means nothing.
    await vi.waitFor(() => expect(component.hasUnsavedChanges()).toBe(false));
  });
});

import { render, screen, fireEvent } from '@testing-library/angular';
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

import { render, screen, fireEvent } from '@testing-library/angular';
import { provideRouter } from '@angular/router';
import { provideLocationMocks } from '@angular/common/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { QueryClient, provideTanStackQuery } from '@tanstack/angular-query-experimental';
import { MessageService } from 'primeng/api';
import { providePrimeNG } from 'primeng/config';
import { describe, expect, it, vi } from 'vitest';
import { of, throwError } from 'rxjs';
import { AuthService } from '@aj-boilerplate/auth';
import { ApiError, ItemsApiService } from '@aj-boilerplate/data-access/api-client';
import type { ItemResponse, PagedResponse } from '@aj-boilerplate/data-access/api-types';
import { ItemListPageComponent } from './item-list-page';

const ITEM: ItemResponse = {
  id: '1',
  name: 'First item',
  description: 'A description',
  status: 'Active',
  createdAt: '2026-01-01T09:00:00Z',
  updatedAt: null,
  rowVersion: 'v1',
};

const ALL_CAPABILITIES = {
  canView: true,
  canCreate: true,
  canEdit: true,
  canDelete: true,
  canAdminister: true,
};

function authStub(capabilities = ALL_CAPABILITIES): Partial<AuthService> {
  return { capabilities: (() => capabilities) as AuthService['capabilities'] };
}

async function renderList(api: Partial<ItemsApiService>, auth = authStub()) {
  return render(ItemListPageComponent, {
    providers: [
      provideZonelessChangeDetection(),
      // A stub target for the post-save/row navigations, so they resolve instead of throwing.
      provideRouter([{ path: 'items', children: [] }]),
      provideLocationMocks(),
      // Retries off: an error-state test should assert the error, not wait out a backoff.
      provideTanStackQuery(new QueryClient({ defaultOptions: { queries: { retry: false } } })),
      providePrimeNG({}),
      MessageService,
      { provide: ItemsApiService, useValue: api },
      { provide: AuthService, useValue: auth },
    ],
  });
}

function paged(items: ItemResponse[]): PagedResponse<ItemResponse> {
  return { items, total: items.length, page: 1, pageSize: 10 };
}

describe('ItemListPageComponent', () => {
  it('renders the rows returned by the API', async () => {
    await renderList({ list: () => of(paged([ITEM])) });

    expect(await screen.findByText('First item')).toBeTruthy();
    expect(screen.getByText('Active')).toBeTruthy();
  });

  it('shows the empty state when the API returns no rows', async () => {
    await renderList({ list: () => of(paged([])) });

    expect(await screen.findByRole('heading', { name: 'No items match' })).toBeTruthy();
  });

  it('shows an error block, not silence, when the list request fails', async () => {
    await renderList({ list: () => throwError(() => new ApiError(500, 'Server exploded', null, null)) });

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain("We couldn't load these items.");
    expect(alert.textContent).toContain('Server exploded');
  });

  it('hides the create action from a user without canCreate', async () => {
    await renderList(
      { list: () => of(paged([ITEM])) },
      authStub({ ...ALL_CAPABILITIES, canCreate: false }),
    );

    await screen.findByText('First item');
    expect(screen.queryByTestId('new-item')).toBeNull();
  });

  it('hides the delete action from a user without canDelete', async () => {
    await renderList(
      { list: () => of(paged([ITEM])) },
      authStub({ ...ALL_CAPABILITIES, canDelete: false }),
    );

    await screen.findByText('First item');
    expect(screen.queryByLabelText('Delete First item')).toBeNull();
  });

  it('asks for confirmation before deleting, and only then calls the API', async () => {
    const remove = vi.fn(() => of(undefined as unknown as void));
    await renderList({ list: () => of(paged([ITEM])), remove });

    await screen.findByText('First item');
    fireEvent.click(screen.getByLabelText('Delete First item'));

    expect(await screen.findByText('Delete this item?')).toBeTruthy();
    expect(remove).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));

    await vi.waitFor(() => expect(remove).toHaveBeenCalledWith('1'));
  });
});

import { render, screen, fireEvent } from '@testing-library/angular';
import { provideZonelessChangeDetection } from '@angular/core';
import { QueryClient, provideTanStackQuery } from '@tanstack/angular-query-experimental';
import { providePrimeNG } from 'primeng/config';
import { describe, expect, it, vi } from 'vitest';
import { of, throwError } from 'rxjs';
import { AdminApiService } from '@aj-boilerplate/data-access/api-client';
import type { MePortals } from '@aj-boilerplate/data-access/api-types';
import { PortalSwitcherComponent } from './portal-switcher';

async function renderSwitcher(result: () => ReturnType<AdminApiService['myPortals']>) {
  const myPortals = vi.fn(result);
  const view = await render(PortalSwitcherComponent, {
    providers: [
      provideZonelessChangeDetection(),
      provideTanStackQuery(new QueryClient()),
      providePrimeNG({}),
      { provide: AdminApiService, useValue: { myPortals } },
    ],
  });
  await vi.waitFor(() => expect(myPortals).toHaveBeenCalled());
  await view.fixture.whenStable();
  return view;
}

const portals = (n: number): MePortals => ({
  portalKeys: Array.from({ length: n }, (_, i) => `p${i}`),
  portals: Array.from({ length: n }, (_, i) => ({ key: `p${i}`, name: i === 0 ? 'Zeta' : `Alpha ${i}`, url: `/p${i}/` })),
});

describe('PortalSwitcherComponent', () => {
  it('is not rendered when the person may open only one portal', async () => {
    await renderSwitcher(() => of(portals(1)));
    expect(screen.queryByTestId('portal-switcher')).toBeNull();
  });

  it('is not rendered when there are none', async () => {
    await renderSwitcher(() => of(portals(0)));
    expect(screen.queryByTestId('portal-switcher')).toBeNull();
  });

  it('is not rendered, and raises no error, when the read fails', async () => {
    await renderSwitcher(() => throwError(() => new Error('down')));
    expect(screen.queryByTestId('portal-switcher')).toBeNull();
  });

  it('lists each portal A-Z as a link to its url when there are two or more', async () => {
    await renderSwitcher(() => of(portals(3)));
    fireEvent.click(await screen.findByRole('button', { name: 'Switch portal' }));
    const links = await screen.findAllByRole('menuitem');
    expect(links.map((l) => l.textContent?.trim())).toEqual(['Alpha 1', 'Alpha 2', 'Zeta']);
    expect(links[2]?.querySelector('a')?.getAttribute('href')).toBe('/p0/');
  });
});

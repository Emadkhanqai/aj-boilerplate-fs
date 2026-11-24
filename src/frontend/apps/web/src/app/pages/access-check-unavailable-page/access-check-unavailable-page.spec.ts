import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { fireEvent, render, screen, waitFor } from '@testing-library/angular';
import { providePrimeNG } from 'primeng/config';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { ApiError } from '@aj-boilerplate/data-access/api-client';
import { AuthService } from '@aj-boilerplate/auth';
import { AccessCheckUnavailablePageComponent } from './access-check-unavailable-page';

async function renderPage(refresh: () => Promise<boolean>, from = '/items?tab=1') {
  const auth = {
    capabilitiesError: signal<unknown>(new ApiError(503, 'unavailable', null)),
    refreshCapabilities: vi.fn(refresh),
  };
  await render(AccessCheckUnavailablePageComponent, {
    providers: [
      provideZonelessChangeDetection(),
      providePrimeNG({}),
      provideRouter([]),
      { provide: AuthService, useValue: auth },
      { provide: ActivatedRoute, useValue: { queryParamMap: of(convertToParamMap({ from })) } },
    ],
  });
  return auth;
}

describe('AccessCheckUnavailablePageComponent', () => {
  it('says this is NOT a permission problem, and states what the server answered', async () => {
    await renderPage(() => Promise.resolve(false));

    expect(screen.getByText(/not a permission problem/i)).toBeTruthy();
    expect(screen.getByTestId('unavailable-reason').textContent).toContain('(503)');
  });

  it('on a successful retry, returns the user to where they were going', async () => {
    await renderPage(() => Promise.resolve(true));
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    fireEvent.click(screen.getByRole('button', { name: /try again/i }));

    await waitFor(() => expect(navigate).toHaveBeenCalledWith('/items?tab=1'));
  });

  it('on a failed retry, stays put and says the service is still not answering', async () => {
    await renderPage(() => Promise.resolve(false));
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    fireEvent.click(screen.getByRole('button', { name: /try again/i }));

    expect(await screen.findByTestId('unavailable-still-failing')).toBeTruthy();
    expect(navigate).not.toHaveBeenCalled();
  });

  it('refuses an off-origin return target', async () => {
    await renderPage(() => Promise.resolve(true), 'https://evil.example/x');
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    fireEvent.click(screen.getByRole('button', { name: /try again/i }));

    await waitFor(() => expect(navigate).toHaveBeenCalledWith('/'));
  });
});

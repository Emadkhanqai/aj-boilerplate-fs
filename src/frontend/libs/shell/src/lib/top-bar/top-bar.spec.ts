import { render, screen, fireEvent } from '@testing-library/angular';
import { provideZonelessChangeDetection } from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { providePrimeNG } from 'primeng/config';
import { AuthService } from '@aj-boilerplate/auth';
import { DocumentDirectionService, LanguageService, provideDocumentDirection } from '@aj-boilerplate/shared/util';
import { TopBarComponent } from './top-bar';

function authStub(): Partial<AuthService> {
  return { roles: () => ['admin'] } as unknown as Partial<AuthService>;
}

async function renderTopBar() {
  return render(TopBarComponent, {
    inputs: { title: 'Items' },
    providers: [
      provideZonelessChangeDetection(),
      providePrimeNG({}),
      provideDocumentDirection(),
      { provide: AuthService, useValue: authStub() },
    ],
  });
}

describe('TopBarComponent — language toggle', () => {
  it('offers Arabic while the UI is in English', async () => {
    await renderTopBar();

    expect(screen.getByRole('button', { name: 'Switch to Arabic' })).toBeTruthy();
  });

  it('flips the document to RTL end-to-end when the toggle is clicked', async () => {
    const { fixture } = await renderTopBar();
    const root = TestBed.inject(DOCUMENT).documentElement;
    // Force the service into existence, as the app initializer does at bootstrap.
    TestBed.inject(DocumentDirectionService);
    await fixture.whenStable();

    fireEvent.click(screen.getByRole('button', { name: 'Switch to Arabic' }));
    await fixture.whenStable();

    // The full chain the user actually experiences: click -> LanguageService signal ->
    // DocumentDirectionService effect -> document attributes -> mirrored CSS.
    expect(root.getAttribute('dir')).toBe('rtl');
    expect(root.getAttribute('lang')).toBe('ar');
  });

  it('offers English back again once Arabic is active', async () => {
    const { fixture } = await renderTopBar();
    TestBed.inject(LanguageService).set('ar');
    await fixture.whenStable();

    // A one-way toggle is a trap: a user who cannot read the UI cannot find their way back.
    expect(screen.getByRole('button', { name: 'Switch to English' })).toBeTruthy();
  });

  it('labels the toggle in the target language script, with an English accessible name', async () => {
    await renderTopBar();

    const toggle = screen.getByRole('button', { name: 'Switch to Arabic' });
    expect(toggle.textContent).toContain('العربية');
  });
});

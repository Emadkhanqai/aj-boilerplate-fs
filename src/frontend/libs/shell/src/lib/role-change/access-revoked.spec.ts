import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { LocationStrategy } from '@angular/common';
import { fireEvent, render, screen } from '@testing-library/angular';
import { providePrimeNG } from 'primeng/config';
import { describe, expect, it, vi } from 'vitest';
import { AuthService } from '@aj-boilerplate/auth';
import { AccessRevokedDialogComponent } from './access-revoked-dialog';
import { AccessRevokedWatcher } from './access-revoked-watcher';
import { ROLE_CHANGE_NAVIGATE, RoleChangeDialogComponent } from './role-change-dialog';
import { RoleChangeWatcher } from './role-change-watcher';

const base = [provideZonelessChangeDetection(), providePrimeNG({})];

describe('AccessRevokedDialogComponent', () => {
  it('renders nothing until access is revoked (positive control: it does render after)', async () => {
    const auth = { signOut: vi.fn() };
    const { fixture } = await render(AccessRevokedDialogComponent, {
      providers: [...base, { provide: AuthService, useValue: auth }],
    });
    expect(screen.queryByTestId('access-revoked-message')).toBeNull();

    TestBed.inject(AccessRevokedWatcher).markRevoked();
    await fixture.whenStable();

    expect(await screen.findByTestId('access-revoked-message')).toBeTruthy();
  });

  it('has exactly one action, and it signs out', async () => {
    const auth = { signOut: vi.fn() };
    const { fixture } = await render(AccessRevokedDialogComponent, {
      providers: [...base, { provide: AuthService, useValue: auth }],
    });
    TestBed.inject(AccessRevokedWatcher).markRevoked();
    await fixture.whenStable();

    const dialog = await screen.findByRole('alertdialog');
    expect(dialog.querySelectorAll('button').length).toBe(1);
    fireEvent.click(await screen.findByRole('button', { name: 'Sign out' }));

    expect(auth.signOut).toHaveBeenCalledTimes(1);
  });
});

describe('RoleChangeDialogComponent', () => {
  it('is hidden while nothing is blocking', async () => {
    await render(RoleChangeDialogComponent, {
      providers: [...base, { provide: RoleChangeWatcher, useValue: { blocking: () => false } }],
    });

    expect(screen.queryByTestId('role-change-message')).toBeNull();
  });

  it('blocks, offers only Reload, and reloads into the app root', async () => {
    const navigate = vi.fn();
    await render(RoleChangeDialogComponent, {
      providers: [
        ...base,
        { provide: RoleChangeWatcher, useValue: { blocking: () => true } },
        { provide: ROLE_CHANGE_NAVIGATE, useValue: navigate },
        { provide: LocationStrategy, useValue: { prepareExternalUrl: (url: string) => url } },
      ],
    });

    const dialog = await screen.findByRole('alertdialog');
    expect(dialog.querySelectorAll('button').length).toBe(1);
    fireEvent.click(await screen.findByRole('button', { name: 'Reload' }));

    expect(navigate).toHaveBeenCalledWith('/');
  });
});

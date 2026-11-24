import { render, screen, fireEvent, within } from '@testing-library/angular';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { of, throwError } from 'rxjs';
import { ApiError } from '@aj-boilerplate/data-access/api-client';
import type { DirectoryMember } from '@aj-boilerplate/data-access/api-types';
import { UsersPageComponent } from './users-page';
import { GROUPS, adminProviders, user } from '../test-utils';

const MEMBERS: DirectoryMember[] = [
  { user: user(1), groups: ['/portal/admins'] },
  { user: user(2, { enabled: false }), groups: ['/portal/viewers', '/portal/editors'] },
];

async function renderPage(over: Record<string, unknown> = {}) {
  const api = {
    groups: () => of({ groups: GROUPS }),
    members: vi.fn(() => of({ members: MEMBERS })),
    searchUsers: vi.fn((q: string) => of({ users: [user(9, { firstName: 'Found', lastName: q })] })),
    setEnabled: vi.fn(() => of(undefined)),
    refreshSessions: vi.fn(() => of(undefined)),
    createUser: vi.fn(() => of({ user: user(5), groups: ['/portal/viewers'] })),
    userGroups: vi.fn(() => of({ groups: ['/portal/admins'], added: [], removed: [] })),
    setUserGroups: vi.fn(() =>
      of({ groups: ['/portal/admins', '/portal/editors'], added: ['/portal/editors'], removed: [] }),
    ),
    ...over,
  };
  const view = await render(UsersPageComponent, { providers: adminProviders(api) });
  return { ...view, api };
}

describe('UsersPageComponent', () => {
  beforeEach(() => localStorage.clear());

  it('lists every member with group names and status when the search is blank', async () => {
    const { api } = await renderPage();
    const rows = await screen.findAllByTestId('user-row');
    expect(rows).toHaveLength(2);
    expect(api.members).toHaveBeenCalled();
    expect(api.searchUsers).not.toHaveBeenCalled();
    expect(rows[0]?.textContent).toContain('First1 Last1');
    expect(rows[0]?.textContent).toContain('Administrators');
    expect(rows[0]?.textContent).toContain('Enabled');
    expect(rows[1]?.textContent).toContain('Disabled');
    expect(rows[1]?.textContent).toContain('Editors, Viewers');
  });

  it('asks the directory with the typed term once the search is not blank', async () => {
    const { api } = await renderPage();
    await screen.findAllByTestId('user-row');
    fireEvent.input(screen.getByTestId('user-search'), { target: { value: 'ann' } });
    await vi.waitFor(() => expect(api.searchUsers).toHaveBeenCalledWith('ann'), { timeout: 2000 });
    expect(await screen.findByText('Found ann')).toBeTruthy();
  });

  it('says so, instead of showing an empty table, when nobody matches', async () => {
    await renderPage({ members: () => of({ members: [] }) });
    expect(await screen.findByRole('heading', { name: 'No one holds a seat yet' })).toBeTruthy();
  });

  it('shows an error block with a retry when the directory is down', async () => {
    await renderPage({ members: () => throwError(() => new ApiError(503, 'Directory unavailable', null, 'UPSTREAM_ERROR')) });
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain("We couldn't load the users.");
    expect(alert.textContent).toContain('Directory unavailable');
  });

  it('confirms before disabling, then calls the API and reports it', async () => {
    const { api } = await renderPage();
    await screen.findAllByTestId('user-row');
    fireEvent.click(screen.getByLabelText('Disable First1 Last1'));
    expect(await screen.findByText('Disable this user?')).toBeTruthy();
    expect(api.setEnabled).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Disable' }));
    await vi.waitFor(() => expect(api.setEnabled).toHaveBeenCalledWith('sub-1', false));
    // The confirm closes once the change went through.
    await vi.waitFor(() => expect(screen.queryByText('Disable this user?')).toBeNull());
  });

  it('offers Enable for a disabled user', async () => {
    const { api } = await renderPage();
    await screen.findAllByTestId('user-row');
    fireEvent.click(screen.getByLabelText('Enable First2 Last2'));
    fireEvent.click(await screen.findByRole('button', { name: 'Enable' }));
    await vi.waitFor(() => expect(api.setEnabled).toHaveBeenCalledWith('sub-2', true));
  });

  it('keeps the confirm open with the reason when the user has no seat (404)', async () => {
    await renderPage({ setEnabled: () => throwError(() => new ApiError(404, 'nope', null, 'NOT_FOUND')) });
    await screen.findAllByTestId('user-row');
    fireEvent.click(screen.getByLabelText('Disable First1 Last1'));
    fireEvent.click(await screen.findByRole('button', { name: 'Disable' }));
    expect((await screen.findByText(/has no seat in the directory/)).textContent).toBeTruthy();
    expect(screen.getByText('Disable this user?')).toBeTruthy();
  });

  it('refreshes sessions after confirmation', async () => {
    const { api } = await renderPage();
    await screen.findAllByTestId('user-row');
    fireEvent.click(screen.getByLabelText('Refresh sessions for First1 Last1'));
    fireEvent.click(await screen.findByRole('button', { name: 'Refresh' }));
    await vi.waitFor(() => expect(api.refreshSessions).toHaveBeenCalledWith('sub-1'));
  });

  it('sorts a column when its header is clicked (name, descending on second click)', async () => {
    await renderPage();
    await screen.findAllByTestId('user-row');
    const names = () => screen.getAllByTestId('user-row').map((r) => within(r).getAllByRole('cell')[0]?.textContent?.trim());
    fireEvent.click(screen.getByRole('columnheader', { name: /^Name/ }));
    await vi.waitFor(() => expect(names()).toEqual(['First1 Last1', 'First2 Last2']));
    fireEvent.click(screen.getByRole('columnheader', { name: /^Name/ }));
    await vi.waitFor(() => expect(names()).toEqual(['First2 Last2', 'First1 Last1']));
  });

  describe('create dialog', () => {
    async function open(over: Record<string, unknown> = {}) {
      const r = await renderPage(over);
      await screen.findAllByTestId('user-row');
      await vi.waitFor(() => expect((screen.getByTestId('add-user').querySelector('button') as HTMLButtonElement).disabled).toBe(false));
      fireEvent.click(screen.getByTestId('add-user').querySelector('button') as HTMLElement);
      await screen.findByText('Add a user');
      return r;
    }

    it('requires an email and a group before it calls the API', async () => {
      const { api } = await open();
      fireEvent.click(screen.getByTestId('cu-submit').querySelector('button') as HTMLElement);
      expect(await screen.findByText('Email is required.')).toBeTruthy();
      expect(screen.getByText('Choose the group this person starts in.')).toBeTruthy();
      expect(api.createUser).not.toHaveBeenCalled();
    });

    it('rejects a malformed email', async () => {
      const { api } = await open();
      fireEvent.input(screen.getByTestId('cu-email'), { target: { value: 'not-an-email' } });
      fireEvent.click(screen.getByTestId('cu-submit').querySelector('button') as HTMLElement);
      expect(await screen.findByText('Enter a valid email address.')).toBeTruthy();
      expect(api.createUser).not.toHaveBeenCalled();
    });

    it('shows the domain message on a 422 and stays open', async () => {
      await open({ createUser: () => throwError(() => new ApiError(422, 'x', null, 'EMAIL_DOMAIN_NOT_ALLOWED')) });
      fireEvent.input(screen.getByTestId('cu-email'), { target: { value: 'a@bad.com' } });
      fireEvent.click(screen.getByTestId('cu-group').querySelector('[role="combobox"]') as HTMLElement);
      fireEvent.click(await screen.findByRole('option', { name: 'Editors' }));
      fireEvent.click(screen.getByTestId('cu-submit').querySelector('button') as HTMLElement);
      expect((await screen.findByTestId('create-error')).textContent).toContain('email domain is not allowed');
      expect(screen.getByText('Add a user')).toBeTruthy();
    });

    it('posts the trimmed values with the chosen group path and closes on success', async () => {
      const { api } = await open();
      fireEvent.input(screen.getByTestId('cu-email'), { target: { value: ' new@example.org ' } });
      fireEvent.click(screen.getByTestId('cu-group').querySelector('[role="combobox"]') as HTMLElement);
      fireEvent.click(await screen.findByRole('option', { name: 'Editors' }));
      fireEvent.click(screen.getByTestId('cu-submit').querySelector('button') as HTMLElement);
      await vi.waitFor(() =>
        expect(api.createUser).toHaveBeenCalledWith({ email: 'new@example.org', firstName: null, lastName: null, groupPath: '/portal/editors' }),
      );
      await vi.waitFor(() => expect(screen.queryByText('Add a user')).toBeNull());
    });
  });

  describe('groups editor', () => {
    it('loads the current groups, saves the full set and shows what was added and removed', async () => {
      const { api } = await renderPage();
      await screen.findAllByTestId('user-row');
      fireEvent.click(screen.getByLabelText('Edit groups for First1 Last1'));
      await vi.waitFor(() => expect(api.userGroups).toHaveBeenCalledWith('sub-1'));
      await screen.findByText('Groups for First1 Last1');
      const save = await vi.waitFor(() => {
        const b = screen.getByTestId('ug-save').querySelector('button') as HTMLButtonElement;
        expect(b.disabled).toBe(false);
        return b;
      });
      fireEvent.click(save);
      await vi.waitFor(() => expect(api.setUserGroups).toHaveBeenCalledWith('sub-1', ['/portal/admins']));
      expect((await screen.findByTestId('groups-result')).textContent).toContain('Added: Editors. Removed: none.');
    });

    it('shows the 404 reason instead of a silent failure', async () => {
      await renderPage({ setUserGroups: () => throwError(() => new ApiError(404, 'x', null, 'NOT_FOUND')) });
      await screen.findAllByTestId('user-row');
      fireEvent.click(screen.getByLabelText('Edit groups for First1 Last1'));
      const save = await vi.waitFor(() => {
        const b = screen.getByTestId('ug-save').querySelector('button') as HTMLButtonElement;
        expect(b.disabled).toBe(false);
        return b;
      });
      fireEvent.click(save);
      expect((await screen.findByTestId('groups-error')).textContent).toContain('no seat in the directory');
    });
  });
});

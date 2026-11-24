import { render, screen, fireEvent, within } from '@testing-library/angular';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { of } from 'rxjs';
import type { AuthHistoryRequest, PagedResponse, AuthenticationSession } from '@aj-boilerplate/data-access/api-types';
import { AdminApiService } from '@aj-boilerplate/data-access/api-client';
import { SignInHistoryPageComponent } from './sign-in-history-page';
import { adminProviders, session } from '../test-utils';

function page(rows: AuthenticationSession[], total = rows.length): PagedResponse<AuthenticationSession> {
  return { items: rows, total, page: 1, pageSize: 20 };
}

async function renderPage(rows: AuthenticationSession[] = [session(1)], total?: number) {
  const authHistory = vi.fn<AdminApiService['authHistory']>(() => of(page(rows, total)));
  const view = await render(SignInHistoryPageComponent, {
    providers: adminProviders({
      authHistory,
      authHistoryUsers: () =>
        of([
          { userId: 'sub-2', userDisplayName: 'Zed Zulu' },
          { userId: 'sub-1', userDisplayName: 'Amy Adams' },
        ]),
    }),
  });
  return { ...view, authHistory };
}

const lastRequest = (fn: ReturnType<typeof vi.fn>): AuthHistoryRequest => fn.mock.calls[fn.mock.calls.length - 1]?.[0] as AuthHistoryRequest;

describe('SignInHistoryPageComponent', () => {
  beforeEach(() => localStorage.clear());

  it('opens on the newest sign-in first, 20 per page', async () => {
    const { authHistory } = await renderPage();
    await screen.findByText('Person 1');
    expect(lastRequest(authHistory)).toMatchObject({ sortBy: 'signedIn', sortDir: 'desc', page: 1, pageSize: 20 });
  });

  it('shows duration formatted, and an open session as Active with no duration', async () => {
    await renderPage([
      session(1, { durationSeconds: 3900 }),
      session(2, { endedAtUtc: null, endReason: null, durationSeconds: null }),
    ]);
    const rows = await screen.findAllByTestId('history-row');
    expect(rows[0]?.textContent).toContain('1h 05m');
    expect(rows[0]?.textContent).toContain('Logged out');
    expect(within(rows[1] as HTMLElement).getByText('Active')).toBeTruthy();
    expect(rows[1]?.textContent).not.toContain('Logged out');
  });

  it.each([
    ['User', 'user'],
    ['Signed in', 'signedIn'],
    ['Ended', 'sessionEnded'],
    ['Duration', 'duration'],
    ['Result', 'result'],
    ['How it ended', 'endReason'],
  ])('sends sortBy=%s -> %s when that column header is clicked', async (header, sortBy) => {
    const { authHistory } = await renderPage();
    await screen.findByText('Person 1');
    // `signedIn` is already the active sort (desc): a first click flips it, others start ascending.
    fireEvent.click(screen.getByRole('columnheader', { name: new RegExp(`^${header}`) }));
    await vi.waitFor(() => expect(lastRequest(authHistory).sortBy).toBe(sortBy));
    expect(lastRequest(authHistory).sortDir).toBe('asc');
  });

  it('renders the standard paginator: count left, page links, rows selector right, 20 selected', async () => {
    await renderPage(Array.from({ length: 20 }, (_, i) => session(i)), 859);
    await screen.findAllByTestId('history-row');
    const paginator = document.querySelector('p-paginator') as HTMLElement;
    await vi.waitFor(() => expect(paginator.querySelector('.p-paginator-current')?.textContent?.trim()).toBe('Showing 1 to 20 of 859'));
    const report = paginator.querySelector('.p-paginator-current') as HTMLElement;
    const pages = paginator.querySelector('.p-paginator-pages') as HTMLElement;
    const rows = paginator.querySelector('p-select, .p-select') as HTMLElement;
    expect(report.textContent?.trim()).toBe('Showing 1 to 20 of 859');
    expect(pages.textContent).toContain('2');
    // Document order is the layout order: count, then page numbers, then the rows selector.
    expect(report.compareDocumentPosition(pages) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(pages.compareDocumentPosition(rows) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(rows.textContent).toContain('20');
    // Below the table only: there is exactly one paginator and it follows the table.
    expect(document.querySelectorAll('p-paginator').length).toBe(1);
    const table = document.querySelector('table') as HTMLElement;
    expect(table.compareDocumentPosition(paginator) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

    fireEvent.click(rows.querySelector('[role="combobox"]') as HTMLElement);
    const options = (await screen.findAllByRole('option')).map((o) => o.textContent?.trim());
    expect(options).toEqual(['10', '20', '50', '100']);
  });

  it('remembers a rows choice, so a re-created page opens with it', async () => {
    const first = await renderPage(Array.from({ length: 20 }, (_, i) => session(i)), 300);
    await screen.findAllByTestId('history-row');
    const select = document.querySelector('p-paginator [role="combobox"]') as HTMLElement;
    fireEvent.click(select);
    fireEvent.click(await screen.findByRole('option', { name: '50' }));
    await vi.waitFor(() => expect(lastRequest(first.authHistory).pageSize).toBe(50));
    expect(localStorage.getItem('aj.pageSize.sign-in-history')).toBe('50');
    first.fixture.destroy();
    TestBed.resetTestingModule();

    const second = await renderPage();
    await screen.findByText('Person 1');
    expect(lastRequest(second.authHistory).pageSize).toBe(50);
  });

  it('falls back to 20 when blocked storage throws', async () => {
    const spy = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    const { authHistory } = await renderPage();
    await screen.findByText('Person 1');
    expect(lastRequest(authHistory).pageSize).toBe(20);
    spy.mockRestore();
  });

  it('filters by user, choosing from an A-Z list of names, and restarts at page 1', async () => {
    const { authHistory } = await renderPage();
    await screen.findByText('Person 1');
    fireEvent.click(document.querySelector('[data-testid="history-user"] [role="combobox"]') as HTMLElement);
    const names = (await screen.findAllByRole('option')).map((o) => o.textContent?.trim());
    expect(names).toEqual(['Amy Adams', 'Zed Zulu']);
    fireEvent.click(screen.getByRole('option', { name: 'Zed Zulu' }));
    await vi.waitFor(() => expect(lastRequest(authHistory).userId).toBe('sub-2'));
    expect(lastRequest(authHistory).page).toBe(1);
  });
});

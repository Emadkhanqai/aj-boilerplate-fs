import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TableModule } from 'primeng/table';
import type { TableLazyLoadEvent } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { SelectModule } from 'primeng/select';
import { DatePickerModule } from 'primeng/datepicker';
import { injectQuery } from '@tanstack/angular-query-experimental';
import { firstValueFrom } from 'rxjs';
import { AdminApiService } from '@aj-boilerplate/data-access/api-client';
import type { AuthHistorySortBy, AuthenticationSession } from '@aj-boilerplate/data-access/api-types';
import { EmptyStateComponent } from '@aj-boilerplate/shared/ui';
import {
  DocumentTitleService,
  PAGE_REPORT_TEMPLATE,
  PAGE_SIZE_OPTIONS,
  formatDateTime,
  formatDuration,
  humanizePascalCase,
  rememberPageSize,
  rememberedPageSize,
  sortByLabel,
} from '@aj-boilerplate/shared/util';
import { adminErrorMessage, rangeToInstants, toSortBy } from '../admin-support';

/** `logged-out` -> `Logged out`. Unknown reasons are shown as the server sent them, never hidden. */
const END_REASON_LABEL: Record<string, string> = {
  'logged-out': 'Logged out',
  'session-expired': 'Session expired',
  'signed-out-by-system': 'Signed out by the system',
};

/**
 * Sign-in history: every sign-in session, server-paged and server-sorted (the API's `sortBy` names
 * are the table's column fields, so a header click is one request). An open session has no end,
 * so it reads "Active" rather than an empty cell.
 */
@Component({
  selector: 'app-sign-in-history-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, TableModule, ButtonModule, SelectModule, DatePickerModule, EmptyStateComponent],
  templateUrl: './sign-in-history-page.html',
})
export class SignInHistoryPageComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly documentTitle = inject(DocumentTitleService);

  protected readonly page = signal(1);
  protected readonly pageSize = signal(rememberedPageSize('sign-in-history'));
  protected readonly pageSizeOptions = PAGE_SIZE_OPTIONS;
  protected readonly pageReport = PAGE_REPORT_TEMPLATE;
  protected readonly sortBy = signal<AuthHistorySortBy>('signedIn');
  protected readonly sortDir = signal<'asc' | 'desc'>('desc');
  protected readonly userId = signal<string | null>(null);
  protected readonly range = signal<(Date | null)[] | null>(null);

  ngOnInit(): void {
    this.documentTitle.set('Sign-in history · Application');
  }

  protected readonly usersQuery = injectQuery(() => ({
    queryKey: ['admin', 'history-users'],
    queryFn: () => firstValueFrom(this.api.authHistoryUsers()),
    meta: { suppressGlobalToast: true },
  }));
  protected readonly userOptions = computed(() =>
    sortByLabel(
      (this.usersQuery.data() ?? []).map((u) => ({ label: u.userDisplayName || u.userId, value: u.userId })),
      (o) => o.label,
    ),
  );

  protected readonly historyQuery = injectQuery(() => {
    const request = {
      ...(this.userId() ? { userId: this.userId() as string } : {}),
      ...rangeToInstants(this.range()),
      sortBy: this.sortBy(),
      sortDir: this.sortDir(),
      page: this.page(),
      pageSize: this.pageSize(),
    };
    return {
      queryKey: ['admin', 'history', request],
      queryFn: () => firstValueFrom(this.api.authHistory(request)),
      meta: { suppressGlobalToast: true },
    };
  });
  protected readonly rows = computed(() => this.historyQuery.data()?.items ?? []);
  protected readonly total = computed(() => this.historyQuery.data()?.total ?? 0);
  protected readonly errorText = computed(() =>
    this.historyQuery.isError() ? adminErrorMessage(this.historyQuery.error(), this.historyQuery.error().message) : '',
  );

  protected onLazyLoad(e: TableLazyLoadEvent): void {
    const rows = e.rows ?? this.pageSize();
    this.pageSize.set(rows);
    rememberPageSize('sign-in-history', rows);
    this.page.set(Math.floor((e.first ?? 0) / rows) + 1);
    this.sortBy.set(toSortBy(e.sortField));
    this.sortDir.set(e.sortOrder === 1 ? 'asc' : 'desc');
  }

  protected setUser(id: string | null): void {
    this.userId.set(id);
    this.page.set(1);
  }

  protected setRange(range: (Date | null)[] | null): void {
    this.range.set(range);
    // A half-picked range is not a filter yet: wait for the end date.
    if (range === null || range[1] !== null) this.page.set(1);
  }

  protected retry(): void {
    void this.historyQuery.refetch();
  }

  protected when(iso: string | null | undefined): string {
    return formatDateTime(iso);
  }
  protected duration(s: AuthenticationSession): string {
    return s.endedAtUtc ? formatDuration(s.durationSeconds) : '—';
  }
  protected reason(s: AuthenticationSession): string {
    if (!s.endedAtUtc) return '—';
    const r = s.endReason ?? '';
    return r === '' ? '—' : (END_REASON_LABEL[r] ?? humanizePascalCase(r));
  }
}

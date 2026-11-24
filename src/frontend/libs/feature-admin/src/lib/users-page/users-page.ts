import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TableModule } from 'primeng/table';
import type { TablePageEvent } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { IconFieldModule } from 'primeng/iconfield';
import { InputIconModule } from 'primeng/inputicon';
import { MessageService } from 'primeng/api';
import { injectMutation, injectQuery, QueryClient } from '@tanstack/angular-query-experimental';
import { firstValueFrom } from 'rxjs';
import { AdminApiService } from '@aj-boilerplate/data-access/api-client';
import type { DirectoryUser } from '@aj-boilerplate/data-access/api-types';
import { ConfirmDialogComponent, EmptyStateComponent } from '@aj-boilerplate/shared/ui';
import {
  DocumentTitleService,
  PAGE_REPORT_TEMPLATE,
  PAGE_SIZE_OPTIONS,
  rememberPageSize,
  rememberedPageSize,
} from '@aj-boilerplate/shared/util';
import { CreateUserDialogComponent } from '../create-user-dialog/create-user-dialog';
import { UserGroupsDialogComponent } from '../user-groups-dialog/user-groups-dialog';
import { adminErrorMessage, toRow } from '../admin-support';
import type { UserRow } from '../admin-support';

type Pending = { kind: 'toggle' | 'refresh'; row: UserRow };

/**
 * Users & roles. Blank search lists everyone who holds a managed seat (members, with their
 * groups); a search term asks the directory instead (names and emails, no groups until the
 * editor opens). The directory answers a blank search with an empty list on purpose, so the two
 * sources are not interchangeable. The table is client-sorted and client-paged: both sources are
 * complete answers, bounded server-side.
 */
@Component({
  selector: 'app-users-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    TableModule,
    ButtonModule,
    InputTextModule,
    IconFieldModule,
    InputIconModule,
    EmptyStateComponent,
    ConfirmDialogComponent,
    CreateUserDialogComponent,
    UserGroupsDialogComponent,
  ],
  templateUrl: './users-page.html',
})
export class UsersPageComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly queryClient = inject(QueryClient);
  private readonly messages = inject(MessageService);
  private readonly documentTitle = inject(DocumentTitleService);

  protected readonly pageSize = signal(rememberedPageSize('admin-users'));
  protected readonly pageSizeOptions = PAGE_SIZE_OPTIONS;
  protected readonly pageReport = PAGE_REPORT_TEMPLATE;
  protected readonly first = signal(0);

  protected readonly searchInput = signal('');
  protected readonly search = signal('');

  protected readonly creating = signal(false);
  protected readonly editing = signal<UserRow | null>(null);
  protected readonly pending = signal<Pending | null>(null);

  private debounce: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    effect((onCleanup) => {
      const raw = this.searchInput();
      this.debounce = setTimeout(() => {
        this.search.set(raw.trim());
        this.first.set(0);
      }, 300);
      onCleanup(() => clearTimeout(this.debounce));
    });
  }

  ngOnInit(): void {
    this.documentTitle.set('Users & roles · Application');
  }

  protected readonly groupsQuery = injectQuery(() => ({
    queryKey: ['admin', 'groups'],
    queryFn: () => firstValueFrom(this.api.groups()),
    meta: { suppressGlobalToast: true },
  }));
  protected readonly groups = computed(() => this.groupsQuery.data()?.groups ?? []);

  /** Raw directory answer as `[user, groups]` pairs; display names are applied in `rows`, so they
   * follow the group list when it arrives after the users. */
  protected readonly usersQuery = injectQuery(() => {
    const q = this.search();
    return {
      queryKey: ['admin', 'users', q],
      queryFn: async (): Promise<[DirectoryUser, string[] | null][]> =>
        q === ''
          ? (await firstValueFrom(this.api.members())).members.map((m): [DirectoryUser, string[]] => [m.user, m.groups])
          : (await firstValueFrom(this.api.searchUsers(q))).users.map((u): [DirectoryUser, null] => [u, null]),
      meta: { suppressGlobalToast: true },
    };
  });
  protected readonly rows = computed(() => {
    const names = new Map(this.groups().map((g) => [g.path, g.name]));
    return (this.usersQuery.data() ?? []).map(([u, g]) => toRow(u, g, (path) => names.get(path) ?? path));
  });

  protected readonly mutation = injectMutation(() => ({
    mutationFn: (p: Pending) =>
      firstValueFrom(
        p.kind === 'toggle' ? this.api.setEnabled(p.row.subject, !p.row.user.enabled) : this.api.refreshSessions(p.row.subject),
      ),
    onSuccess: async (_: unknown, p: Pending) => {
      this.pending.set(null);
      this.messages.add({
        severity: 'success',
        summary:
          p.kind === 'refresh'
            ? `Sessions refreshed for ${p.row.name}`
            : `${p.row.name} ${p.row.user.enabled ? 'disabled' : 'enabled'}`,
        life: 4000,
      });
      await this.reload();
    },
    meta: { suppressGlobalToast: true },
  }));

  protected readonly mutationError = computed(() =>
    this.mutation.isError() ? adminErrorMessage(this.mutation.error(), 'That did not go through.') : null,
  );

  protected readonly confirmCopy = computed(() => {
    const p = this.pending();
    if (!p) return null;
    if (p.kind === 'refresh') {
      return { title: 'Refresh sessions?', message: `${p.row.name}'s open sessions will re-read their access on the next request.`, label: 'Refresh', danger: false };
    }
    return p.row.user.enabled
      ? { title: 'Disable this user?', message: `${p.row.name} will be unable to sign in until you enable them again.`, label: 'Disable', danger: true }
      : { title: 'Enable this user?', message: `${p.row.name} will be able to sign in again.`, label: 'Enable', danger: false };
  });

  protected ask(kind: Pending['kind'], row: UserRow): void {
    this.mutation.reset();
    this.pending.set({ kind, row });
  }

  protected confirm(): void {
    const p = this.pending();
    if (p) this.mutation.mutate(p);
  }

  protected onPage(e: TablePageEvent): void {
    this.first.set(e.first);
    this.pageSize.set(e.rows);
    rememberPageSize('admin-users', e.rows);
  }

  protected async reload(): Promise<void> {
    await this.queryClient.invalidateQueries({ queryKey: ['admin'] });
  }

  protected async onCreated(): Promise<void> {
    this.creating.set(false);
    this.messages.add({ severity: 'success', summary: 'User added', life: 4000 });
    await this.reload();
  }

  protected retry(): void {
    void this.usersQuery.refetch();
  }
}

import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { TableModule } from 'primeng/table';
import type { TableLazyLoadEvent } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { IconFieldModule } from 'primeng/iconfield';
import { InputIconModule } from 'primeng/inputicon';
import { SelectModule } from 'primeng/select';
import { MessageService } from 'primeng/api';
import { injectMutation, injectQuery, QueryClient } from '@tanstack/angular-query-experimental';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '@aj-boilerplate/auth';
import { ItemsApiService, apiErrorMessage, isConflictError } from '@aj-boilerplate/data-access/api-client';
import type { ItemResponse, ItemStatus } from '@aj-boilerplate/data-access/api-types';
import { ITEM_STATUSES } from '@aj-boilerplate/data-access/api-types';
import { ConfirmDialogComponent, EmptyStateComponent, StatusPillComponent } from '@aj-boilerplate/shared/ui';
import { DocumentTitleService, formatDateTime, sortByLabel } from '@aj-boilerplate/shared/util';

/**
 * ============================================================================
 * SAMPLE FEATURE — delete `libs/feature-items` (and its routes, nav entries,
 * `ItemsApiService`, and the `scope:feature-items` eslint entries) once you
 * have a real feature. It exists to be read and copied, not kept.
 * ============================================================================
 *
 * A server-paged, server-searched list. The things worth copying:
 *
 * - **Server-side paging and search.** The query key includes page/pageSize/search, so TanStack
 *   Query caches each page separately and refetches only when one of them changes. Never fetch
 *   everything and filter in the browser.
 * - **All four states are handled**: loading (the table's own skeleton), error (an inline block
 *   with a retry), empty (`app-empty-state`), and success. A data view that only handles
 *   "success" is not finished.
 * - **Debounced search.** Typing does not fire a request per keystroke.
 * - **Delete is confirmed** through the app's own dialog, never `window.confirm()`.
 * - **PrimeNG only.** No bare `<input>`, `<select>`, or `<button>` anywhere in the template.
 */
@Component({
  selector: 'app-item-list-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    FormsModule,
    TableModule,
    ButtonModule,
    InputTextModule,
    IconFieldModule,
    InputIconModule,
    SelectModule,
    StatusPillComponent,
    EmptyStateComponent,
    ConfirmDialogComponent,
  ],
  templateUrl: './item-list-page.html',
})
export class ItemListPageComponent implements OnInit {
  private readonly api = inject(ItemsApiService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly queryClient = inject(QueryClient);
  private readonly messages = inject(MessageService);
  private readonly documentTitle = inject(DocumentTitleService);

  /** Capability-derived, never a role check. UX only — the API authorizes independently. */
  protected readonly canCreate = computed(() => this.auth.capabilities().canCreate);
  protected readonly canEdit = computed(() => this.auth.capabilities().canEdit);
  protected readonly canDelete = computed(() => this.auth.capabilities().canDelete);

  protected readonly page = signal(1);
  protected readonly pageSize = signal(10);
  /** Bound to the search box. Kept separate from {@link search} so typing doesn't fetch. */
  protected readonly searchInput = signal('');
  /** The debounced value the query actually keys on. */
  protected readonly search = signal('');

  /** Status filter — client-side, because the sample API does not accept a status parameter.
   * Extend the API and move this server-side before the list grows past one page's worth. */
  protected readonly statusFilter = signal<ItemStatus | null>(null);
  protected readonly statusOptions = sortByLabel(
    ITEM_STATUSES.map((s) => ({ label: s, value: s })),
    (o) => o.label,
  );

  protected readonly pendingDelete = signal<ItemResponse | null>(null);

  private debounce: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    effect((onCleanup) => {
      const raw = this.searchInput();
      this.debounce = setTimeout(() => {
        this.search.set(raw.trim());
        this.page.set(1);
      }, 300);
      onCleanup(() => clearTimeout(this.debounce));
    });
  }

  ngOnInit(): void {
    this.documentTitle.set('Items · Application');
  }

  protected readonly itemsQuery = injectQuery(() => ({
    queryKey: ['items', this.page(), this.pageSize(), this.search()],
    queryFn: () =>
      firstValueFrom(
        this.api.list({ page: this.page(), pageSize: this.pageSize(), search: this.search() }),
      ),
    // This page renders its own error block below, so the app-wide error toast would double up.
    meta: { suppressGlobalToast: true },
  }));

  /** The rows actually rendered, after the client-side status filter. */
  protected readonly rows = computed<ItemResponse[]>(() => {
    const all = this.itemsQuery.data()?.items ?? [];
    const status = this.statusFilter();
    return status === null ? all : all.filter((i) => i.status === status);
  });

  protected readonly total = computed(() => this.itemsQuery.data()?.total ?? 0);

  protected readonly deleteMutation = injectMutation(() => ({
    mutationFn: (id: string) => firstValueFrom(this.api.remove(id)),
    onSuccess: async () => {
      this.pendingDelete.set(null);
      this.messages.add({ severity: 'success', summary: 'Item deleted', life: 4000 });
      await this.queryClient.invalidateQueries({ queryKey: ['items'] });
    },
    meta: { suppressGlobalToast: true },
  }));

  /** A delete that lost a race is a conflict too — say so precisely instead of "delete failed". */
  protected readonly deleteError = computed(() => {
    const error: unknown = this.deleteMutation.error();
    if (error === null) {
      return null;
    }
    return isConflictError(error)
      ? apiErrorMessage(error, '')
      : apiErrorMessage(error, 'Could not delete this item.');
  });

  protected onLazyLoad(event: TableLazyLoadEvent): void {
    const rows = event.rows ?? this.pageSize();
    this.pageSize.set(rows);
    this.page.set(Math.floor((event.first ?? 0) / rows) + 1);
  }

  protected formatWhen(iso: string | null): string {
    return formatDateTime(iso);
  }

  protected openItem(item: ItemResponse): void {
    void this.router.navigate(['/items', item.id]);
  }

  protected askDelete(item: ItemResponse): void {
    this.deleteMutation.reset();
    this.pendingDelete.set(item);
  }

  protected cancelDelete(): void {
    this.pendingDelete.set(null);
  }

  protected confirmDelete(): void {
    const item = this.pendingDelete();
    if (item !== null) {
      this.deleteMutation.mutate(item.id);
    }
  }

  protected retry(): void {
    void this.itemsQuery.refetch();
  }
}

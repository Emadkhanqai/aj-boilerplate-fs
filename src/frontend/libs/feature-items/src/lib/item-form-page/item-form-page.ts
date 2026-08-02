import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { SelectModule } from 'primeng/select';
import { MessageService } from 'primeng/api';
import { Message } from 'primeng/message';
import { injectMutation, injectQuery, QueryClient } from '@tanstack/angular-query-experimental';
import { firstValueFrom } from 'rxjs';
import { ItemsApiService, apiErrorMessage, isConflictError } from '@aj-boilerplate/data-access/api-client';
import type { CreateItemRequest, ItemResponse, ItemStatus, UpdateItemRequest } from '@aj-boilerplate/data-access/api-types';
import { ITEM_STATUSES } from '@aj-boilerplate/data-access/api-types';
import { DocumentTitleService, sortByLabel } from '@aj-boilerplate/shared/util';

/**
 * ============================================================================
 * SAMPLE FEATURE — delete `libs/feature-items` once you have a real feature.
 * ============================================================================
 *
 * One component serving both create and edit, keyed off the `:id` route param. The things worth
 * copying:
 *
 * - **A typed reactive form** (`nonNullable`), with validation declared once on the controls and
 *   errors rendered per field only after the control is touched.
 * - **Optimistic concurrency, surfaced honestly.** The `rowVersion` read with the item is sent
 *   back on save. When the server answers `409`, the user is told *someone else changed this*
 *   and offered a reload — not a generic "save failed", which would invite them to just try
 *   again and clobber the other change.
 * - **PrimeNG only** — `pInputText`, `pTextarea`, `p-select`, `p-button`. No native controls.
 * - The dropdown is searchable and sorted A–Z (`sortByLabel`), the workspace default.
 */
@Component({
  selector: 'app-item-form-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ButtonModule, InputTextModule, TextareaModule, SelectModule, Message],
  templateUrl: './item-form-page.html',
})
export class ItemFormPageComponent implements OnInit {
  private readonly api = inject(ItemsApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder);
  private readonly queryClient = inject(QueryClient);
  private readonly messages = inject(MessageService);
  private readonly documentTitle = inject(DocumentTitleService);

  /** `null` on the create route; the item id on the edit route. */
  protected readonly itemId = signal<string | null>(this.route.snapshot.paramMap.get('id'));
  protected readonly isEdit = computed(() => this.itemId() !== null);

  protected readonly statusOptions = sortByLabel(
    ITEM_STATUSES.map((s) => ({ label: s, value: s })),
    (o) => o.label,
  );

  /** The `rowVersion` last read from the server. Sent on save; refreshed after a save or reload. */
  private readonly rowVersion = signal<string | null>(null);

  /** Set when a save is rejected as a concurrency conflict. Drives the reload banner. */
  protected readonly conflict = signal(false);

  protected readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(200)]],
    description: ['', [Validators.maxLength(1000)]],
    status: ['Draft' as ItemStatus, [Validators.required]],
  });

  protected readonly itemQuery = injectQuery(() => ({
    queryKey: ['items', 'detail', this.itemId()],
    queryFn: () => firstValueFrom(this.api.getById(this.itemId() as string)),
    enabled: this.itemId() !== null,
    meta: { suppressGlobalToast: true },
  }));

  constructor() {
    // Seed the form from the loaded item, once, whenever fresh server data arrives. Also runs
    // after a conflict reload, which is what replaces the user's stale values with current ones.
    effect(() => {
      const item = this.itemQuery.data();
      if (item !== undefined) {
        this.applyItem(item);
      }
    });
  }

  ngOnInit(): void {
    this.documentTitle.set(this.isEdit() ? 'Edit item · Application' : 'New item · Application');
  }

  private applyItem(item: ItemResponse): void {
    this.form.setValue({
      name: item.name,
      description: item.description ?? '',
      status: item.status,
    });
    this.form.markAsPristine();
    this.rowVersion.set(item.rowVersion);
  }

  protected readonly saveMutation = injectMutation(() => ({
    mutationFn: (): Promise<ItemResponse> => {
      const value = this.form.getRawValue();
      const description = value.description.trim() === '' ? null : value.description.trim();
      const id = this.itemId();
      if (id === null) {
        const body: CreateItemRequest = { name: value.name.trim(), description, status: value.status };
        return firstValueFrom(this.api.create(body));
      }
      const body: UpdateItemRequest = {
        name: value.name.trim(),
        description,
        status: value.status,
        // The whole point: send back exactly what we read, so the server can detect a lost update.
        rowVersion: this.rowVersion() ?? '',
      };
      return firstValueFrom(this.api.update(id, body));
    },
    onSuccess: async (saved: ItemResponse) => {
      this.conflict.set(false);
      this.applyItem(saved);
      this.messages.add({
        severity: 'success',
        summary: this.isEdit() ? 'Item updated' : 'Item created',
        life: 4000,
      });
      await this.queryClient.invalidateQueries({ queryKey: ['items'] });
      void this.router.navigate(['/items']);
    },
    onError: (error: unknown) => {
      // A conflict is not a validation failure and must not read like one. Flag it so the
      // template can offer the only correct recovery: reload and look at the current values.
      this.conflict.set(isConflictError(error));
    },
    meta: { suppressGlobalToast: true },
  }));

  protected readonly saveError = computed(() => {
    const error: unknown = this.saveMutation.error();
    return error === null ? null : apiErrorMessage(error, 'Could not save this item.');
  });

  protected showError(control: 'name' | 'description'): boolean {
    const c = this.form.controls[control];
    return c.invalid && (c.touched || c.dirty);
  }

  protected save(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      return;
    }
    this.saveMutation.mutate();
  }

  /** Discards the local edit and reloads the server's current values — the conflict recovery. */
  protected reloadFromServer(): void {
    this.conflict.set(false);
    this.saveMutation.reset();
    void this.itemQuery.refetch();
  }

  protected cancel(): void {
    void this.router.navigate(['/items']);
  }
}

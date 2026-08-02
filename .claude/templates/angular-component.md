# Template: Angular Feature Component

Standalone · `OnPush` · signals · `inject()` · typed reactive forms · PrimeNG only · generated
API types.

**Read `src/frontend/DESIGN.md` before writing any component.**

## The data-access service

```ts
// libs/data-access/api-client/src/lib/items.service.ts
import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import type { components } from '@aj-boilerplate/data-access/api-types'; // GENERATED

export type ItemResponse = components['schemas']['ItemResponse'];

@Injectable({ providedIn: 'root' })
export class ItemsService {
  private readonly http = inject(HttpClient);

  readonly search = signal('');

  /** The envelope is unwrapped centrally by an interceptor; this sees `data`. */
  readonly items = rxResource({
    request: () => ({ search: this.search() }),
    loader: ({ request }) =>
      this.http.get<ItemResponse[]>('/api/v1/items', { params: request }),
  });

  create(name: string, description: string | null) {
    return this.http.post<ItemResponse>('/api/v1/items', { name, description });
  }
}
```

## The component

```ts
// libs/feature-items/src/lib/item-list/item-list.component.ts
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { MessageModule } from 'primeng/message';
import { ProgressSpinnerModule } from 'primeng/progressspinner';
import { ItemsService } from '@aj-boilerplate/data-access/api-client';

@Component({
  selector: 'aj-item-list',
  standalone: true,
  imports: [TableModule, ButtonModule, InputTextModule, MessageModule, ProgressSpinnerModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './item-list.component.html',
})
export class ItemListComponent {
  private readonly items = inject(ItemsService);

  protected readonly rows = this.items.items;
  protected readonly isEmpty = computed(
    () => !this.rows.isLoading() && (this.rows.value()?.length ?? 0) === 0,
  );

  protected onSearch(term: string): void {
    this.items.search.set(term);
  }
}
```

```html
<!-- item-list.component.html — all four states, always -->
<label class="sr-only" for="item-search">Search items</label>
<input
  pInputText
  id="item-search"
  type="search"
  [value]="items.search()"
  (input)="onSearch($any($event.target).value)"
/>

@if (rows.isLoading()) {
  <p-progressSpinner ariaLabel="Loading items" />
} @else if (rows.error()) {
  <p-message severity="error" text="Could not load items. Please try again." />
} @else if (isEmpty()) {
  <p-message severity="info" text="No items yet." />
} @else {
  <p-table [value]="rows.value() ?? []" [paginator]="true" [rows]="20">
    <ng-template pTemplate="header">
      <tr><th scope="col">Name</th><th scope="col">Status</th></tr>
    </ng-template>
    <ng-template pTemplate="body" let-item>
      <tr><td>{{ item.name }}</td><td>{{ item.status }}</td></tr>
    </ng-template>
  </p-table>
}
```

## A typed reactive form

```ts
private readonly fb = inject(FormBuilder);

protected readonly form = this.fb.nonNullable.group({
  name: ['', [Validators.required, Validators.maxLength(200)]],
  description: [''],
});

protected submit(): void {
  if (this.form.invalid) { this.form.markAllAsTouched(); return; }

  this.saving.set(true);
  const { name, description } = this.form.getRawValue();

  this.items.create(name, description || null).subscribe({
    error: (err) => {
      // Map the envelope's errors[] back onto the controls: the server is the authority.
      for (const message of err.error?.errors ?? []) {
        this.form.controls.name.setErrors({ server: message });
      }
      this.saving.set(false);
    },
    complete: () => this.saving.set(false),
  });
}
```

## Rules

- **No `HttpClient` in a component** — always through `data-access/api-client`.
- **Types come from `api-types`, generated from OpenAPI.** Never hand-write a backend type;
  never edit a generated file; regenerate with [`/sync`](../commands/sync.md).
- **Versioned endpoints only** (`/api/v1/...`).
- **PrimeNG for every control.** No native `<select>`, no hand-rolled dropdown or dialog.
  Dropdowns are searchable and A–Z sorted by default.
- **All four states handled** — loading, error, empty, success. An unhandled empty state is an
  incomplete feature.
- **Typed reactive forms only**; disable submit while in flight; map server errors back to
  controls.
- **Accessibility is a requirement:** labelled controls, semantic markup, keyboard operability,
  visible focus, and an axe-core run on the finished screen.
- **Under ~300 lines.** Past that, extract a child component or move logic into the service.
- User-facing strings go through i18n; layouts tolerate RTL.

See [`../standards/angular.md`](../standards/angular.md) and
[`../standards/typescript.md`](../standards/typescript.md).

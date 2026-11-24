import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { MultiSelectModule } from 'primeng/multiselect';
import { injectMutation, injectQuery } from '@tanstack/angular-query-experimental';
import { firstValueFrom } from 'rxjs';
import { AdminApiService } from '@aj-boilerplate/data-access/api-client';
import type { DirectoryGroup, UserGroupsResult } from '@aj-boilerplate/data-access/api-types';
import { adminErrorMessage, groupOptions } from '../admin-support';

/** Edit the full set of managed groups one person holds, then show what the save added and removed. */
@Component({
  selector: 'app-user-groups-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DialogModule, ButtonModule, MultiSelectModule],
  template: `
    <p-dialog
      [visible]="true"
      [modal]="true"
      [closable]="false"
      [style]="{ width: '520px' }"
      appendTo="body"
      [header]="'Groups for ' + userName()"
    >
      @if (current.isPending()) {
        <div class="load-block" role="status">Loading groups…</div>
      } @else if (current.isError()) {
        <div class="error-block" role="alert" data-testid="groups-load-error">{{ loadError() }}</div>
      } @else {
        <div class="dialog-form">
          @if (saveError(); as message) {
            <div class="error-block" role="alert" data-testid="groups-error">{{ message }}</div>
          }
          <div class="field">
            <label for="ug-groups">Groups</label>
            <p-multiselect
              inputId="ug-groups"
              [options]="options()"
              optionLabel="label"
              optionValue="value"
              placeholder="No groups"
              [filter]="true"
              filterBy="label"
              appendTo="body"
              display="chip"
              [ngModel]="selected()"
              (ngModelChange)="selected.set($event)"
              data-testid="ug-groups"
            />
            <span class="hint">Saving replaces the whole set. Removing every group removes the person's seat.</span>
          </div>
          @if (result(); as r) {
            <div class="result-note" role="status" data-testid="groups-result">
              Saved. Added: {{ list(r.added) }}. Removed: {{ list(r.removed) }}.
            </div>
          }
        </div>
      }
      <ng-template pTemplate="footer">
        @if (result()) {
          <p-button label="Close" (onClick)="closed.emit()" />
        } @else {
          <p-button label="Cancel" severity="secondary" [text]="true" [disabled]="save.isPending()" (onClick)="closed.emit()" />
          <p-button
            [label]="save.isPending() ? 'Saving…' : 'Save groups'"
            [disabled]="save.isPending() || !current.isSuccess()"
            data-testid="ug-save"
            (onClick)="save.mutate()"
          />
        }
      </ng-template>
    </p-dialog>
  `,
})
export class UserGroupsDialogComponent {
  private readonly api = inject(AdminApiService);

  readonly subject = input.required<string>();
  readonly userName = input.required<string>();
  readonly groups = input.required<DirectoryGroup[]>();
  readonly saved = output<void>();
  readonly closed = output<void>();

  protected readonly options = computed(() => groupOptions(this.groups()));
  protected readonly selected = signal<string[]>([]);
  protected readonly result = signal<UserGroupsResult | null>(null);

  protected readonly current = injectQuery(() => ({
    queryKey: ['admin', 'user-groups', this.subject()],
    queryFn: () => firstValueFrom(this.api.userGroups(this.subject())),
    meta: { suppressGlobalToast: true },
  }));

  constructor() {
    effect(() => {
      const data = this.current.data();
      if (data) this.selected.set([...data.groups]);
    });
  }

  protected readonly save = injectMutation(() => ({
    mutationFn: () => firstValueFrom(this.api.setUserGroups(this.subject(), this.selected())),
    onSuccess: (r: UserGroupsResult) => {
      this.result.set(r);
      this.saved.emit();
    },
    meta: { suppressGlobalToast: true },
  }));

  protected readonly loadError = computed(() => adminErrorMessage(this.current.error(), 'Could not load this person’s groups.'));
  protected readonly saveError = computed(() =>
    this.save.isError() ? adminErrorMessage(this.save.error(), 'Could not save the groups.') : null,
  );

  protected list(paths: string[]): string {
    const names = new Map(this.groups().map((g) => [g.path, g.name]));
    return paths.length === 0 ? 'none' : paths.map((p) => names.get(p) ?? p).join(', ');
  }
}

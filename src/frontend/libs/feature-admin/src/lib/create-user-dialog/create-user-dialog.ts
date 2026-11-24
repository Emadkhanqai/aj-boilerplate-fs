import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { injectMutation } from '@tanstack/angular-query-experimental';
import { firstValueFrom } from 'rxjs';
import { AdminApiService } from '@aj-boilerplate/data-access/api-client';
import type { DirectoryGroup } from '@aj-boilerplate/data-access/api-types';
import { adminErrorMessage, groupOptions } from '../admin-support';

/** Give a person a seat: email, optional names, and the group they start in. */
@Component({
  selector: 'app-create-user-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, DialogModule, ButtonModule, InputTextModule, SelectModule],
  template: `
    <p-dialog
      [visible]="true"
      [modal]="true"
      [closable]="false"
      [style]="{ width: '480px' }"
      appendTo="body"
      header="Add a user"
    >
      <form [formGroup]="form" (ngSubmit)="submit()" novalidate class="dialog-form">
        @if (error(); as message) {
          <div class="error-block" role="alert" data-testid="create-error">{{ message }}</div>
        }
        <div class="field">
          <label for="cu-email">Email<span class="req" aria-hidden="true">*</span></label>
          <input id="cu-email" pInputText type="email" formControlName="email" autocomplete="off" data-testid="cu-email" />
          @if (showError('email')) {
            <span class="err" role="alert">
              {{ form.controls.email.hasError('required') ? 'Email is required.' : 'Enter a valid email address.' }}
            </span>
          }
        </div>
        <div class="form-grid two">
          <div class="field">
            <label for="cu-first">First name</label>
            <input id="cu-first" pInputText formControlName="firstName" autocomplete="off" />
          </div>
          <div class="field">
            <label for="cu-last">Last name</label>
            <input id="cu-last" pInputText formControlName="lastName" autocomplete="off" />
          </div>
        </div>
        <div class="field">
          <label for="cu-group">Group<span class="req" aria-hidden="true">*</span></label>
          <p-select
            inputId="cu-group"
            formControlName="groupPath"
            [options]="options()"
            optionLabel="label"
            optionValue="value"
            placeholder="Choose a group"
            [filter]="true"
            filterBy="label"
            appendTo="body"
            data-testid="cu-group"
          />
          @if (showError('groupPath')) {
            <span class="err" role="alert">Choose the group this person starts in.</span>
          }
        </div>
      </form>
      <ng-template pTemplate="footer">
        <p-button label="Cancel" severity="secondary" [text]="true" [disabled]="create.isPending()" (onClick)="closed.emit()" />
        <p-button
          [label]="create.isPending() ? 'Adding…' : 'Add user'"
          [disabled]="create.isPending()"
          data-testid="cu-submit"
          (onClick)="submit()"
        />
      </ng-template>
    </p-dialog>
  `,
})
export class CreateUserDialogComponent {
  private readonly api = inject(AdminApiService);
  private readonly fb = inject(FormBuilder).nonNullable;

  readonly groups = input.required<DirectoryGroup[]>();
  readonly created = output<void>();
  readonly closed = output<void>();

  protected readonly options = computed(() => groupOptions(this.groups()));

  protected readonly form = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
    firstName: [''],
    lastName: [''],
    groupPath: ['', Validators.required],
  });

  protected readonly create = injectMutation(() => ({
    mutationFn: () => {
      const v = this.form.getRawValue();
      return firstValueFrom(
        this.api.createUser({
          email: v.email.trim(),
          firstName: v.firstName.trim() || null,
          lastName: v.lastName.trim() || null,
          groupPath: v.groupPath,
        }),
      );
    },
    onSuccess: () => this.created.emit(),
    meta: { suppressGlobalToast: true },
  }));

  protected readonly error = computed(() =>
    this.create.isError() ? adminErrorMessage(this.create.error(), 'Could not add this user.') : null,
  );

  protected showError(name: 'email' | 'groupPath'): boolean {
    const c = this.form.controls[name];
    return c.invalid && (c.touched || c.dirty);
  }

  protected submit(): void {
    this.form.markAllAsTouched();
    if (this.form.valid && !this.create.isPending()) this.create.mutate();
  }
}

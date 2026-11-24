import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterModule } from '@angular/router';
import { ToastModule } from 'primeng/toast';
import { AccessRevokedDialogComponent, RoleChangeDialogComponent } from '@aj-boilerplate/shell';

@Component({
  imports: [RouterModule, ToastModule, RoleChangeDialogComponent, AccessRevokedDialogComponent],
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {}

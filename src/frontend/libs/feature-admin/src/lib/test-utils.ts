import { provideZonelessChangeDetection } from '@angular/core';
import { QueryClient, provideTanStackQuery } from '@tanstack/angular-query-experimental';
import { MessageService } from 'primeng/api';
import { providePrimeNG } from 'primeng/config';
import { AdminApiService } from '@aj-boilerplate/data-access/api-client';
import type { AuthenticationSession, DirectoryGroup, DirectoryUser } from '@aj-boilerplate/data-access/api-types';

/** Shared spec providers: zoneless, no query retries (an error test asserts the error, not a backoff). */
export function adminProviders(api: Partial<AdminApiService>) {
  return [
    provideZonelessChangeDetection(),
    provideTanStackQuery(new QueryClient({ defaultOptions: { queries: { retry: false } } })),
    providePrimeNG({}),
    MessageService,
    { provide: AdminApiService, useValue: api },
  ];
}

export const GROUPS: DirectoryGroup[] = [
  { path: '/portal/viewers', name: 'Viewers' },
  { path: '/portal/admins', name: 'Administrators' },
  { path: '/portal/editors', name: 'Editors' },
];

export const user = (n: number, over: Partial<DirectoryUser> = {}): DirectoryUser => ({
  subject: `sub-${n}`,
  username: `user${n}`,
  email: `user${n}@example.org`,
  firstName: `First${n}`,
  lastName: `Last${n}`,
  enabled: true,
  ...over,
});

export const session = (n: number, over: Partial<AuthenticationSession> = {}): AuthenticationSession => ({
  id: `id-${n}`,
  userId: `sub-${n}`,
  userDisplayName: `Person ${n}`,
  signedInAtUtc: '2026-11-20T08:00:00Z',
  endedAtUtc: '2026-11-20T09:05:00Z',
  endReason: 'logged-out',
  result: 'Success',
  durationSeconds: 3900,
  ...over,
});

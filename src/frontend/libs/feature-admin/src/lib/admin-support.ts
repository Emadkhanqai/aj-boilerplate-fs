import { ApiError } from '@aj-boilerplate/data-access/api-client';
import type {
  AuthHistorySortBy,
  DirectoryGroup,
  DirectoryUser,
} from '@aj-boilerplate/data-access/api-types';
import { sortByLabel } from '@aj-boilerplate/shared/util';

/** One line per documented failure of the admin endpoints; anything else gets the caller's fallback. */
export function adminErrorMessage(err: unknown, fallback: string): string {
  if (!(err instanceof ApiError)) return fallback;
  if (err.status === 422) return 'That email domain is not allowed. Use an address on an approved domain.';
  if (err.status === 404) return 'That person has no seat in the directory, so there is nothing to change.';
  if (err.status === 400) return 'The request was not valid. Check the fields and try again.';
  if (err.status === 502 || err.status === 503) {
    return 'The directory service is not answering right now. Nothing was changed; try again shortly.';
  }
  return fallback;
}

export interface UserRow {
  user: DirectoryUser;
  subject: string;
  name: string;
  email: string;
  status: 'Enabled' | 'Disabled';
  /** `null` for a search hit — the search endpoint does not return groups; the editor loads them. */
  groups: string[] | null;
  groupsText: string;
}

export function displayName(u: DirectoryUser): string {
  const full = [u.firstName, u.lastName].filter((p): p is string => !!p).join(' ');
  return full || u.username;
}

export function toRow(user: DirectoryUser, groups: string[] | null, labelOf: (path: string) => string): UserRow {
  return {
    user,
    subject: user.subject,
    name: displayName(user),
    email: user.email ?? '',
    status: user.enabled ? 'Enabled' : 'Disabled',
    groups,
    groupsText: groups === null ? '' : sortByLabel(groups.map(labelOf), (g) => g).join(', '),
  };
}

/** Group options for a `p-select` / `p-multiselect`, A–Z by name. */
export function groupOptions(groups: DirectoryGroup[]): { label: string; value: string }[] {
  return sortByLabel(
    groups.map((g) => ({ label: g.name, value: g.path })),
    (o) => o.label,
  );
}

/** `p-table` sort field -> the API's `sortBy`. The table's `field` IS the API name, so this only guards the type. */
const SORTABLE: readonly AuthHistorySortBy[] = ['signedIn', 'user', 'sessionEnded', 'duration', 'result', 'endReason'];
export function toSortBy(field: unknown): AuthHistorySortBy {
  return SORTABLE.find((s) => s === field) ?? 'signedIn';
}

/**
 * A picked calendar range -> the API's instants. `from` is inclusive and `to` EXCLUSIVE, so an
 * end day of the 5th must send the start of the 6th or the 5th's own sign-ins fall off the end.
 * Calendar components are read as UTC days, matching `DISPLAY_TIME_ZONE`.
 */
export function rangeToInstants(range: (Date | null)[] | null): { from?: string; to?: string } {
  const [start, end] = range ?? [];
  const day = (d: Date, plus: number): string =>
    new Date(Date.UTC(d.getFullYear(), d.getMonth(), d.getDate() + plus)).toISOString();
  return {
    ...(start ? { from: day(start, 0) } : {}),
    ...(end || start ? { to: day((end ?? start) as Date, 1) } : {}),
  };
}

import type { Capabilities } from './roles';

/**
 * What a change in the caller's capabilities means for a user who is already inside the app:
 *
 * - `reduction` — at least one capability was LOST (whether or not another was gained). The server
 *   already enforces the narrower set, so the screen the user is on may be one they can no longer
 *   use: block and ask for a reload.
 * - `increase` — capabilities were only GAINED. Nothing on screen became forbidden: a toast, and
 *   the capabilities refresh in place.
 * - `unchanged` — same set, or there was no earlier answer to compare against, so no diff can
 *   honestly be made.
 */
export type RoleChangeKind = 'reduction' | 'increase' | 'unchanged';

const KEYS: readonly (keyof Capabilities)[] = ['canView', 'canCreate', 'canEdit', 'canDelete', 'canAdminister'];

/** `before` is `null` when `/me` had never answered before the recheck. */
export function classifyRoleChange(before: Capabilities | null, after: Capabilities): RoleChangeKind {
  if (before === null) {
    return 'unchanged';
  }
  if (KEYS.some((k) => before[k] && !after[k])) {
    return 'reduction';
  }
  return KEYS.some((k) => after[k] && !before[k]) ? 'increase' : 'unchanged';
}

/** The two capability sets around a forced `/me` refetch — see `AuthService.recheckCapabilities`. */
export interface RoleRecheck {
  readonly before: Capabilities | null;
  readonly after: Capabilities;
}

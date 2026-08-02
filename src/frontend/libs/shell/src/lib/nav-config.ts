import type { Capability } from '@aj-boilerplate/auth';

export interface NavItem {
  label: string;
  to: string;
  /** `end` matches the path exactly — use it for index routes and for leaf routes that sit
   * underneath another item's prefix (e.g. `/items/new` beneath `/items`). */
  end?: boolean;
  /**
   * Custom active predicate. When set it fully decides the highlight, overriding the default
   * prefix matching. Used by "Items" so it stays active on `/items` and on a detail/edit route,
   * but NOT on `/items/new` — which is its own nav item.
   */
  activeWhen?: (pathname: string) => boolean;
  /** PrimeIcons class name, e.g. `pi pi-home`. */
  icon: string;
  /**
   * Capability required to SEE this item. Omitted = visible to every authenticated user.
   *
   * This is presentation only. The backend enforces the permission on the underlying route and
   * on every API call it makes — hiding a link here protects nothing.
   */
  requiredCapability?: Capability;
}

export interface NavGroup {
  label: string;
  items: NavItem[];
}

/**
 * The application's primary navigation. Add a group per area of the product; keep the number of
 * top-level items small enough to scan.
 *
 * The Items entries are the SAMPLE FEATURE — delete them with `libs/feature-items`.
 */
export const NAV_GROUPS: readonly NavGroup[] = [
  {
    label: 'Workspace',
    items: [
      { label: 'Home', to: '/', end: true, icon: 'pi pi-home' },
      {
        label: 'Items',
        to: '/items',
        activeWhen: (p) => p === '/items' || (p.startsWith('/items/') && !p.startsWith('/items/new')),
        icon: 'pi pi-box',
      },
      {
        label: 'New Item',
        to: '/items/new',
        end: true,
        icon: 'pi pi-plus',
        requiredCapability: 'canCreate',
      },
    ],
  },
  {
    label: 'Administration',
    items: [
      // Placeholder — wire to your own admin surface, or delete the group.
      {
        label: 'Settings',
        to: '/settings',
        icon: 'pi pi-cog',
        requiredCapability: 'canAdminister',
      },
    ],
  },
];

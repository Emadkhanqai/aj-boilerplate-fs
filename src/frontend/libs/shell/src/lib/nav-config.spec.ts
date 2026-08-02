import { describe, expect, it } from 'vitest';
import { NAV_GROUPS } from './nav-config';

describe('NAV_GROUPS', () => {
  const allItems = NAV_GROUPS.flatMap((g) => g.items);

  it('gives every item a label, a route and an icon', () => {
    for (const item of allItems) {
      expect(item.label).not.toBe('');
      expect(item.to.startsWith('/')).toBe(true);
      expect(item.icon).toMatch(/^pi /);
    }
  });

  it('has no duplicate routes', () => {
    const routes = allItems.map((i) => i.to);
    expect(new Set(routes).size).toBe(routes.length);
  });

  it('keeps the Items entry active on a detail route but not on the New Item route', () => {
    const items = allItems.find((i) => i.to === '/items');

    expect(items?.activeWhen?.('/items')).toBe(true);
    expect(items?.activeWhen?.('/items/42')).toBe(true);
    expect(items?.activeWhen?.('/items/new')).toBe(false);
  });
});

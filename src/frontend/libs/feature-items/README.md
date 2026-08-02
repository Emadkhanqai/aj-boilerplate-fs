# feature-items — the sample feature

**This library exists to be read, copied, and then deleted.** It is a complete vertical slice
against `/api/v1/items`, showing the conventions every real feature should follow.

## Deleting it

1. `rm -rf libs/feature-items`
2. Remove the `items` routes from `apps/web/src/app/app.routes.ts`.
3. Remove the Items entries from `libs/shell/src/lib/nav-config.ts`.
4. Remove `ItemsApiService` from `libs/data-access/api-client` (file + `src/index.ts` export).
5. Remove the item types from `libs/data-access/api-types` (or just regenerate).
6. Remove the `@aj-boilerplate/feature-items` path from `tsconfig.base.json`, and the
   `scope:feature-items` entries from `eslint.config.mjs`.
7. Remove the item handlers from `apps/web/src/mocks/handlers.ts` and the journey from
   `apps/web-e2e`.

## What it demonstrates

| Concern | Where |
|---|---|
| Server-side paging + debounced search | `item-list-page.ts` (query key includes page/size/search) |
| Loading / error / empty / success states | `item-list-page.html` — all four, always |
| Typed reactive form + per-field validation | `item-form-page.ts` (`fb.nonNullable.group`) |
| Optimistic concurrency (409) surfaced to the user | `item-form-page.ts` `conflict` signal + the reload banner |
| Capability-gated actions | `canCreate` / `canEdit` / `canDelete` from `AuthService` |
| Confirmed destructive action | `app-confirm-dialog`, never `window.confirm()` |
| PrimeNG-only controls, searchable A–Z dropdowns | both templates |

## The concurrency rule

`ItemResponse.rowVersion` is read with the item and sent back on `PUT`. If the server answers
`409`, the user is told plainly that **someone else changed this record**, and the only offered
action is to reload the current values. Never retry a rejected write silently — that is how one
user's change quietly erases another's.

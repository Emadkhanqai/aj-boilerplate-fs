# CLAUDE.md — frontend

Conventions for `src/frontend`. Read this and `DESIGN.md` before writing any UI.

## Stack

Angular 21 · Nx monorepo · PrimeNG 21 · TanStack Query · Vitest · Playwright · MSW.

## Layout

```
apps/
  web/          the application: bootstrap, routes, design CSS, MSW mocks, public auth pages
  web-e2e/      Playwright journeys + the axe-core accessibility gate
libs/
  auth/                    session, guards, role -> capability map
  data-access/api-types/   GENERATED from OpenAPI. Never hand-edited.
  data-access/api-client/  the only place that talks HTTP
  shared/ui/               presentational components, no feature knowledge
  shared/util/             formatters and helpers, no UI, no HTTP
  shell/                   sidebar, top bar, layout, nav config
  feature-items/           SAMPLE FEATURE — read it, copy it, delete it
```

**Import direction is enforced, not advisory:** `app → feature → shared`. `shared/*` never imports
a feature; features never import each other. The rule lives in `@nx/enforce-module-boundaries`
(`eslint.config.mjs`) and lint fails if you break it. If two features need the same thing, it
moves to `shared/*` — it does not get imported sideways.

## Non-negotiables

1. **Standalone components, signals, `OnPush`, `inject()`.** No NgModules. No constructor
   injection. No `ChangeDetectionStrategy.Default`.
2. **Strict TypeScript. No `any`, ever** — not in tests, not "temporarily". Use `unknown` and
   narrow it.
3. **PrimeNG only.** No bare `<button>`, `<input>`, `<select>`, `<textarea>`, or hand-rolled
   `<table>` in a template. Dropdowns are searchable and A–Z sorted (`sortByLabel`) by default.
4. **API types are generated.** `npm run generate:api` writes
   `libs/data-access/api-types/src/lib/types.ts`. Never hand-edit it, and never re-declare a
   backend DTO anywhere else. If you need a shape the API does not expose, change the API.
5. **Versioned endpoints only** — `/api/v1/...`.
6. **The `ApiResponse<T>` envelope is unwrapped centrally** by `envelopeInterceptor`. Feature code
   never touches `.data`. A `success: false` body becomes a thrown `ApiError` whatever the HTTP
   status.
7. **Every data view handles loading, error, empty, and success.** All four. See
   `libs/feature-items/src/lib/item-list-page/item-list-page.html`.
8. **Role-aware UI is never a security boundary.** Capability checks (`auth.capabilities()`) hide
   things for clarity; the backend authorizes every request independently. Never implement a
   permission by hiding a button.
9. **No `innerHTML` with user content.** Angular escapes interpolation by default — keep it that
   way. `[innerHTML]`/`bypassSecurityTrust*` need an explicit review comment justifying them.
10. **Components ≤ 300 lines.** Past that, split: a container that fetches and a presentational
    child, or extract pure logic into a `*-support.ts` file that can be unit-tested directly.
11. **No secrets in the repo.** `src/environments/*` and `public/env.js` hold placeholders only;
    real values arrive at runtime via `docker/40-env.sh`.

## Optimistic concurrency

Any editable record carries a `rowVersion`. Read it with the record, send it back on update, and
when the server answers **409**, tell the user plainly that *someone else changed this* and offer
a reload. Never retry a rejected write silently — that is how one user's change erases another's.
`apiErrorMessage()` already produces the right copy; `item-form-page.ts` shows the whole pattern.

## State

- **Server state → TanStack Query.** Query keys include every parameter that changes the result
  (page, page size, search). Invalidate after mutations; do not hand-patch the cache unless you
  can explain why.
- **Local UI state → signals.** `signal`, `computed`, `effect` (sparingly — an `effect` that
  writes signals usually wants to be a `computed`).
- No global mutable stores.

## Commands

```sh
npm install

npx nx serve web                      # dev server against a real backend
npx nx serve web --configuration=demo # offline: MSW-mocked API, no backend needed
npx nx build web                      # production build
npx nx run-many -t lint --all         # lint everything, incl. module boundaries
npx nx run-many -t test --all         # all unit tests
npx nx e2e web-e2e                    # Playwright journeys + axe (boots the demo build)
npm run generate:api                  # regenerate API types from the backend's OpenAPI
```

## Definition of done

- `npx nx build web` succeeds with **no warnings**.
- `npx nx run-many -t lint --all` and `-t test --all` pass.
- Generated API types match the current OpenAPI document (regenerate, commit the diff).
- All four view states handled; keyboard-navigable; axe suite green.
- No `any`, no duplicated DTOs, no secrets.
- **Nothing is pushed without explicit approval.**

## Adding a feature library

```sh
npx nx g @nx/angular:library --directory=libs/feature-<name> --standalone --prefix=app
```

Then: tag it `scope:feature-<name>` in `project.json`, add that tag to `eslint.config.mjs` (both
the `scope:app` allow-list and its own entry), add the path alias to `tsconfig.base.json`, add a
lazy route in `apps/web/src/app/app.routes.ts`, and add a nav entry in
`libs/shell/src/lib/nav-config.ts`. `libs/feature-items` is the worked example of all five.

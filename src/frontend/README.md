# Frontend — Angular + Nx + PrimeNG

The web application. An Nx monorepo: one app (`apps/web`), one e2e project (`apps/web-e2e`), and
a set of libraries with an enforced import direction.

> Read [`CLAUDE.md`](./CLAUDE.md) for the engineering conventions and
> [`DESIGN.md`](./DESIGN.md) for the visual contract **before** writing code.

## Quick start

```sh
npm install
npx nx serve web --configuration=demo   # runs offline against MSW mocks — no backend needed
```

Open http://localhost:4200. The `demo` configuration ships a role picker instead of a real
identity provider — pick any of the three sample users.

To run against a real backend instead:

```sh
npx nx serve web
```

Requests go to relative `/api/v1/...` paths, so proxy them to your API (see `nginx.conf` for the
container equivalent).

## Layout

| Path | What it is |
|---|---|
| `apps/web` | Bootstrap, routing, design CSS, PrimeNG theme, MSW handlers, public auth pages |
| `apps/web-e2e` | Playwright journeys and the axe-core accessibility gate |
| `libs/auth` | Session, route guards, role -> capability map |
| `libs/data-access/api-types` | **Generated** from OpenAPI. Never hand-edited. |
| `libs/data-access/api-client` | The only code that talks HTTP |
| `libs/shared/ui` | Presentational components |
| `libs/shared/util` | Formatters and helpers |
| `libs/shell` | Sidebar, top bar, layout, navigation config |
| `libs/feature-items` | **Sample feature** — read it, copy its shape, then delete it |

## Commands

```sh
npx nx build web                 # production build
npx nx run-many -t lint --all    # lint, including module-boundary rules
npx nx run-many -t test --all    # unit tests
npx nx e2e web-e2e               # Playwright + accessibility (boots the demo build itself)
npm run generate:api             # regenerate API types from the backend's OpenAPI document
```

## First things to change

1. Fill in `DESIGN.md`, then update `apps/web/src/design/tokens.css` and
   `apps/web/src/styles/app-preset.ts` to match.
2. Replace the brand block in `libs/shell/src/lib/sidebar/sidebar.ts` and the hero copy in
   `apps/web/src/app/pages/login-page/login-page.ts`.
3. Replace `ROLES` and `Capabilities` in `libs/auth/src/lib/roles.ts` with your permission model.
4. Point `generate:api` at your backend and regenerate the types.
5. Build your first feature, then delete `libs/feature-items` (its README lists every step).

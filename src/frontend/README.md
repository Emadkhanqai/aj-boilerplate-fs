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
identity provider — pick any of the three sample users. It also mocks one "What's new"
announcement, so the feature spotlight appears on the first route you land on; that sample lives in
`apps/web/src/mocks/handlers.ts` and is a mock, not seed data.

**To see the real sign-in**, run the whole stack from the repository root: `docker compose up --build`,
then open http://localhost:4200 and sign in as `admin`, `editor`, `viewer` or `noaccess` (see the
root README). `npx nx serve web` on its own starts the app with `AUTH_MODE=dev` (the role picker)
and has no proxy to an API, so it is only useful with the mocks.

## Sign-in

Deployed builds hold **no token**: nginx in the web container checks an HttpOnly session cookie
against the Auth service on every request and injects the API's `Authorization` header itself. The
container reads these at start (`docker/16-nginx-profile.sh`, `docker/40-env.sh`):

| Variable | Default | Meaning |
|---|---|---|
| `AUTH_MODE` | `gateway` | `gateway`, or `dev` for the role picker (UI work only; an ungated `/api/` proxy) |
| `AUTH_PROVIDER` | `embedded` | `embedded`: this container is the gateway. `usp`: static files only; USP's shared gateway does the rest |
| `API_UPSTREAM` | `http://api:8080` | where `/api/` goes |
| `AUTH_UPSTREAM` | `http://auth:8080` | the Auth service (`embedded` only) |

The frontend expects the backend in this repository: the API (`/api/v1/...`, including
`GET /api/v1/me`) and, for `embedded`, the Auth service. The contract is
`docs/auth/gateway-contract.md`; the SPA side is `libs/auth`.

## Layout

| Path | What it is |
|---|---|
| `apps/web` | Bootstrap, routing, design CSS, PrimeNG theme, MSW handlers, public pages (login, signing out, access denied) |
| `apps/web-e2e` | Playwright journeys and the axe-core accessibility gate |
| `libs/auth` | Gateway/dev providers, route guards, role -> capability map (no tokens) |
| `libs/data-access/api-types` | **Generated** from OpenAPI. Never hand-edited. |
| `libs/data-access/api-client` | The only code that talks HTTP |
| `libs/shared/ui` | Presentational components, including the "What's new" spotlight modal |
| `libs/shared/util` | Formatters and helpers, plus `LanguageService` |
| `libs/shell` | Sidebar, top bar, layout, navigation config — and the "What's new" wiring |
| `libs/feature-items` | **Sample feature** — read it, copy its shape, then delete it |

## Commands

```sh
npx nx build web                 # production build
npx nx run-many -t lint --all    # lint, including module-boundary rules
npx nx run-many -t test --all    # unit tests
npx nx e2e web-e2e               # Playwright + accessibility (boots the demo build itself)
npm run generate:api             # regenerate generated.ts from docs/api/openapi.json
```

## First things to change

1. Fill in `DESIGN.md`, then update `apps/web/src/design/tokens.css` and
   `apps/web/src/styles/app-preset.ts` to match.
2. Replace the brand block in `libs/shell/src/lib/sidebar/sidebar.ts` and the hero copy in
   `apps/web/src/app/pages/login-page/login-page.ts`.
3. Replace `ROLES` and `Capabilities` in `libs/auth/src/lib/roles.ts` with your permission model.
4. Point `generate:api` at your backend and regenerate the types.
5. Build your first feature, then delete `libs/feature-items` (its README lists every step).

# Standard: Angular Frontend

**Target:** Angular (latest LTS) + Nx + **PrimeNG**, in `src/frontend/`, workspace scope
`@aj-boilerplate`.

## Read this before building any UI

**`src/frontend/DESIGN.md` is mandatory reading before writing a single component.** It carries
the design language — tokens, spacing scale, typography, colour roles, component patterns,
empty/loading/error treatments. An agent that builds UI without reading it will produce
something that looks nothing like the rest of the app, and that is treated as a defect, not a
nit. If `DESIGN.md` is still a template, fill it in *first*.

## Workspace architecture

```
src/frontend/
├── apps/
│   ├── web/                    # the application shell
│   └── web-e2e/                # Playwright end-to-end suite
└── libs/
    ├── auth/                   # guard, HTTP interceptor, token handling
    ├── data-access/
    │   ├── api-client/         # thin typed wrappers over the generated client
    │   └── api-types/          # GENERATED from OpenAPI — never hand-edited
    ├── shared/ui/              # presentational components (PrimeNG compositions)
    ├── shared/util/            # pure helpers, no Angular dependencies
    ├── shell/                  # layout, sidebar, top bar, nav config
    └── feature-items/          # the sample feature — copy it, then delete it
```

### Import direction (enforced by Nx module boundaries)

| Layer | May import | Must NOT import |
|---|---|---|
| `apps/*` | any lib | — |
| `feature-*` | `data-access/*`, `shared/*`, `auth` | another `feature-*`, `shell` |
| `shell` | `shared/*`, `auth` | any `feature-*` |
| `data-access/*` | `shared/util` | any `feature-*`, `shared/ui` |
| `shared/ui` | `shared/util` | anything else |
| `shared/util` | *(nothing)* | everything |

Enforced by `@nx/enforce-module-boundaries` with tags. A wrong-direction import fails lint,
and lint failure is a build failure.

## Components

- **Standalone components only.** No `NgModule` in new code; `standalone: true` is the
  default and `imports` are declared on the component.
- **`ChangeDetectionStrategy.OnPush` on every component.** No exceptions — if a component
  "needs" default change detection, its state model is wrong.
- **`inject()` over constructor injection.** It composes with functional guards, interceptors,
  and resolvers, and it keeps constructors free for real initialisation.
- **Components stay under ~300 lines.** Past that, split: extract a child component, move
  logic to a service, or move a pure transform to `shared/util`. Long components are where
  untested logic hides.
- One component per file. Colocate its spec and styles.

```ts
@Component({
  selector: 'aj-item-list',
  standalone: true,
  imports: [TableModule, ButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './item-list.component.html',
})
export class ItemListComponent {
  private readonly items = inject(ItemsService);

  protected readonly query = signal('');
  protected readonly rows = this.items.list;                       // resource/signal
  protected readonly filtered = computed(() =>
    this.rows().filter((r) => r.name.includes(this.query())));
}
```

## State — signals first

- **Signals are the default state primitive:** `signal`, `computed`, `linkedSignal`,
  `resource`/`httpResource` for async reads, and `effect` only for genuine side effects.
- **NgRx (or any global store) requires an ADR.** Do not reach for a store because the app
  "will get big". Record the decision in `docs/adr/` with the specific problem signals could
  not solve, then adopt it.
- Component state stays in the component; shared state lives in a `providedIn: 'root'` service
  exposing `readonly` signals and explicit mutation methods. Never expose a writable signal
  across a library boundary.
- Prefer `async` pipe / signal reads in templates over manual subscription. If you subscribe
  manually, use `takeUntilDestroyed()`.

## PrimeNG is the only component library

- **PrimeNG for every interactive control.** No native `<select>`, `<input type="checkbox">`,
  `<button>` for real actions, or hand-rolled dropdown/dialog/table. Consistency and
  accessibility both come from using one library properly.
- Do not add a second UI library. A gap in PrimeNG is filled by composing PrimeNG primitives in
  `shared/ui`, not by installing an alternative.
- **Dropdowns/selects are searchable (`[filter]="true"`) and A–Z sorted by default** unless a
  meaningful domain order exists.
- Theme via PrimeNG design tokens defined in one place; never override component internals
  with `::ng-deep` scattered through feature styles.

## Forms

- **Typed reactive forms only** (`FormBuilder.nonNullable`, `FormGroup<T>`). No template-driven
  forms, no untyped `FormGroup`, no `any` in a form model.
- Validation messages come from a shared error-message map, not inline strings per field.
- Server-side validation errors (`errors[]` from the `ApiResponse` envelope) are mapped back
  onto the corresponding controls — the server is the authority, the client is a convenience.
- Disable submit while a request is in flight; never rely on the user not double-clicking.

## API layer — generated, never hand-written

- **Types and clients are generated from the backend OpenAPI document** into
  `libs/data-access/api-types`. **Hand-written HTTP clients are prohibited**, and so is
  hand-copying a backend DTO into a TypeScript interface.
- Regenerate with the `/sync` command after any API change. Generated files are committed but
  **never hand-edited** — an edit is silently destroyed on the next regeneration.
- Feature code calls a service in `data-access/api-client`; it never injects `HttpClient`
  directly.
- **Consume versioned endpoints only** (`/api/v1/...`).
- Unwrap the `ApiResponse<T>` envelope **centrally** in an interceptor or a base client, and
  surface `traceId` in the user-facing error detail so support can correlate it.
- Handle **loading / error / empty / success** for every data view. An unhandled empty state is
  an incomplete feature.

## Security in the UI

- **Role-aware UI is UX, never security.** The backend enforces every permission; the UI only
  avoids showing the user a door they cannot open. Never implement a restriction by hiding
  alone (see [`owasp-security.md`](owasp-security.md)).
- **No `bypassSecurityTrustHtml` / `innerHTML` with user content** unless it is sanitised and
  the exception is reviewed. Angular escapes by default — keep it that way.
- Tokens are held by the `auth` lib and attached by an interceptor. No component reads a token,
  and nothing sensitive is persisted to `localStorage` without a reviewed reason.

## TypeScript & tooling

- **`strict: true`**, and **no `any`** — see [`typescript.md`](typescript.md). An `any` is a
  review blocker, not a nit.
- **ESLint + Prettier configuration is committed** and CI runs both. Formatting is not a matter
  of taste and is never argued about in review — the `auto-format` hook fixes it on save.
- Path aliases mirror the library structure (`@aj-boilerplate/shared/ui`).

## Testing

- **Vitest** for services, signals, pipes, and component logic. Test behaviour, not
  implementation.
- **Playwright** (`apps/web-e2e`) for user journeys. Every new route gets at least one journey.
- **axe-core accessibility checks on every new or changed screen.** A new screen that has never
  been run through axe is not done. Semantic markup, labelled controls, keyboard operability,
  and visible focus are requirements, not enhancements.
- Type-checking (`nx run-many -t typecheck`) is part of the test surface.

## Internationalisation

User-facing strings go through i18n from day one and layouts tolerate RTL, even when only one
language ships. Retrofitting either is far more expensive than doing it up front.

## Commands

```bash
npx nx run-many -t lint typecheck test build     # the local loop
npx nx affected -t test --base=origin/main       # what the hook runs
npx nx e2e web-e2e                                # Playwright
```

## Related

[`typescript.md`](typescript.md) · [`api-design.md`](api-design.md) · [`swagger-openapi.md`](swagger-openapi.md) · [`testing.md`](testing.md) · [`owasp-security.md`](owasp-security.md) · [`../templates/angular-component.md`](../templates/angular-component.md)

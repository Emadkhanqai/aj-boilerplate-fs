# shared/ui

Presentational components with no feature knowledge. A component belongs here only once a SECOND
feature needs it — until then it lives in the feature that owns it.

| Component | Selector | Notes |
|---|---|---|
| `StatusPillComponent` | `app-status-pill` | Maps a status string to a tone class. Edit `toneFor` for your statuses. |
| `ConfirmDialogComponent` | `app-confirm-dialog` | The app's own yes/no modal in place of `window.confirm()`. |
| `EmptyStateComponent` | `app-empty-state` | The "nothing here yet" panel body, with an optional action slot. |
| `QUERY_CLIENT` | — | The shared TanStack `QueryClient`, wired to toast otherwise-unhandled API errors. |

## Rules

- **PrimeNG only.** No bare `<button>`, `<input>`, or `<select>` — use `p-button`, `pInputText`,
  `p-select`. Consistency of focus, disabled, and keyboard behaviour is the whole point.
- Styling comes from `apps/web/src/design/components.css` and the tokens it reads. Do not
  redefine colours in a component.
- No `HttpClient`, no routing decisions, no feature imports.

# shared/ui

Presentational components with no feature knowledge. A component belongs here only once a SECOND
feature needs it — until then it lives in the feature that owns it.

| Component | Selector | Notes |
|---|---|---|
| `StatusPillComponent` | `app-status-pill` | Maps a status string to a tone class. Edit `toneFor` for your statuses. |
| `ConfirmDialogComponent` | `app-confirm-dialog` | The app's own yes/no modal in place of `window.confirm()`. |
| `EmptyStateComponent` | `app-empty-state` | The "nothing here yet" panel body, with an optional action slot. |
| `WhatsNewModalComponent` | `app-whats-new-modal` | "What's new" feature spotlight, mounted by the shell on every route change. Bespoke markup + CSS — see the exception below. |
| `QUERY_CLIENT` | — | The shared TanStack `QueryClient`, wired to toast otherwise-unhandled API errors. |
| `GlobalErrorHandler` | — | The app's `ErrorHandler`, via `provideGlobalErrorHandler()`. Logs with context, reports to the optional `ERROR_MONITOR` seam, and raises a deduplicated toast. Covers what TanStack Query does not: throws from effects, subscribes, and event handlers. |

## Rules

- **PrimeNG only.** No bare `<button>`, `<input>`, or `<select>` — use `p-button`, `pInputText`,
  `p-select`. Consistency of focus, disabled, and keyboard behaviour is the whole point.
- Styling comes from `apps/web/src/design/components.css` and the tokens it reads. Do not
  redefine colours in a component.

### The one exception: `whats-new-modal`

`WhatsNewModalComponent` breaks both rules above, deliberately and with sign-off. It is a one-off
announcement surface — gradient hero, confetti, seven keyframe animations — whose visual design
*is* the deliverable, has no PrimeNG equivalent, and must not read as product chrome, so its
literal colours belong in its own stylesheet rather than in the shared token set. The exception is
scoped to that single component, is restated in its class comment, and explicitly does **not**
cover accessibility: the component carries `role="dialog"`, `aria-modal`, `aria-labelledby`, a
labelled close button, and `role="tab"`/`aria-selected` dots itself, precisely because there is no
PrimeNG component supplying them. Do not treat it as precedent — the next bespoke-looking request
is still PrimeNG-only until someone argues this case again.

The full reasoning, the alternatives, and the costs — including the raised `anyComponentStyle`
budget — are recorded in
[ADR-0007](../../../../../docs/adr/0007-bespoke-whats-new-modal.md). What the component is *for* is
in [`docs/whats-new.md`](../../../../../docs/whats-new.md).
- No `HttpClient`, no routing decisions, no feature imports.

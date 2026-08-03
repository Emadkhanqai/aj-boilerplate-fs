# shell

The authenticated application chrome: sidebar, top bar, and the routed content area.

- `nav-config.ts` — **the navigation.** Add your product's entries here; capability-gate them with
  `requiredCapability`, remembering that hiding a link is UX, not security.
- `app-layout.ts` — the layout component the `authGuard`-protected route group renders into. It
  also owns the route -> page-title mapping (`metaForPath`), the redirect on session expiry, and
  the "What's new" sweep: `unack(path)` on every `NavigationEnd`, `ack(ids)` on dismiss. That check
  belongs here, not in a page — it must run on every route change whatever is mounted, and one
  announcement can be scoped to several unrelated pages. Note that the pending list is only ever
  set to a non-empty value and is cleared **only** on a deliberate dismiss; see
  [`docs/whats-new.md`](../../../../docs/whats-new.md).
- `sidebar.ts` / `top-bar.ts` — presentation only.

Public routes (login, auth callback, signing out, 404) deliberately render OUTSIDE this shell.

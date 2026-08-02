# shell

The authenticated application chrome: sidebar, top bar, and the routed content area.

- `nav-config.ts` — **the navigation.** Add your product's entries here; capability-gate them with
  `requiredCapability`, remembering that hiding a link is UX, not security.
- `app-layout.ts` — the layout component the `authGuard`-protected route group renders into. It
  also owns the route -> page-title mapping (`metaForPath`) and the redirect on session expiry.
- `sidebar.ts` / `top-bar.ts` — presentation only.

Public routes (login, auth callback, signing out, 404) deliberately render OUTSIDE this shell.

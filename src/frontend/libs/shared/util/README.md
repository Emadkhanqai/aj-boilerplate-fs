# shared/util

Framework-light helpers with no UI and no API knowledge. Anything here must be usable from any
feature without dragging a dependency along.

- `format.ts` — dates, byte sizes, initials, PascalCase humanising. Set `DISPLAY_TIME_ZONE` once.
- `sort-by-label.ts` — the A–Z ordering every dropdown uses by default.
- `validate-positive-int.ts` — one shared numeric field check, so error copy never drifts.
- `download.ts` — trigger a browser download from a `Blob`.
- `document-title.service.ts` — set the page title from a route component's `ngOnInit`.

Do NOT put business calculations here. A rule that belongs to a feature belongs in that feature —
or, if the server owns it, on the server.

# shared/util

Framework-light helpers with no UI and no API knowledge. Anything here must be usable from any
feature without dragging a dependency along.

- `format.ts` — dates, byte sizes, initials, PascalCase humanising. Set `DISPLAY_TIME_ZONE` once.
- `sort-by-label.ts` — the A–Z ordering every dropdown uses by default.
- `validate-positive-int.ts` — one shared numeric field check, so error copy never drifts.
- `download.ts` — trigger a browser download from a `Blob`.
- `document-title.service.ts` — set the page title from a route component's `ngOnInit`.
- `language.service.ts` — which language the UI is showing (`'en' | 'ar'`, English by default)
  plus `pick(en, ar)` for API payloads that carry paired `*En`/`*Ar` fields. Deliberately not an
  i18n framework; if the product ever needs message catalogues, adopt a real library and delete
  this.

Do NOT put business calculations here. A rule that belongs to a feature belongs in that feature —
or, if the server owns it, on the server.

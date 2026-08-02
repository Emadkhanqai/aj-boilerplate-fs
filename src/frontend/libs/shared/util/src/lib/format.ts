/**
 * Presentation formatters. Locale-aware throughout so a second language can be added without
 * rewriting call sites — pass `locale` explicitly where the caller knows better than the default.
 *
 * Nothing here formats money: currency rounding is a decision your product must make once,
 * deliberately, and match against whatever the server computes. Add it here when you do, with a
 * comment recording which server-side rule it mirrors.
 */

const EM_DASH = '—';

/**
 * The timezone timestamps render in. UTC by default so two people in different places never read
 * the same instant differently. Change this to the business's operating timezone if that is the
 * shared frame of reference — and if you do, any surface using these helpers MUST carry a visible
 * timezone hint, since the formatted value itself gives no cue.
 */
export const DISPLAY_TIME_ZONE = 'UTC';

/** An explicit UTC designator or numeric offset closing an ISO instant. */
const ZONE_DESIGNATOR = /(?:Z|[+-]\d{2}:?\d{2})$/i;

/** An ISO string that carries a time-of-day part (and so denotes an instant, not a bare date). */
const HAS_TIME_PART = /T\d{2}:\d{2}/;

/**
 * Parses an API timestamp as the UTC instant it actually is.
 *
 * A serializer that emits a zoneless `2026-07-28T08:09:00` is common (e.g. .NET's
 * `DateTimeKind.Unspecified`), and `new Date(...)` silently parses that as the VIEWER's local
 * time — which then converts to the display zone from the wrong base, so every rendered timestamp
 * is off by the viewer's offset. Anchoring the zoneless form to UTC fixes that; strings that
 * already carry a designator are left exactly as sent.
 *
 * A bare date like `2026-08-01` is left alone too: ECMAScript already parses the date-only form as
 * UTC midnight, whereas appending `Z` would produce an unparseable string.
 */
function parseUtcInstant(iso: string): Date {
  const anchored = HAS_TIME_PART.test(iso) && !ZONE_DESIGNATOR.test(iso) ? `${iso}Z` : iso;
  return new Date(anchored);
}

/** Formats a date (no time) as e.g. "07 Jul 2026". Returns an em dash for nullish/empty input,
 * and the raw string back when it cannot be parsed (better than showing "Invalid Date"). */
export function formatDate(iso: string | null | undefined, locale = 'en-GB'): string {
  if (iso === null || iso === undefined || iso === '') {
    return EM_DASH;
  }
  const d = parseUtcInstant(iso);
  if (Number.isNaN(d.getTime())) {
    return iso;
  }
  return d.toLocaleDateString(locale, {
    day: '2-digit',
    month: 'short',
    year: 'numeric',
    timeZone: DISPLAY_TIME_ZONE,
  });
}

/** Formats a date + time as e.g. "07 Jul 2026, 14:30". Same null/parse behaviour as {@link formatDate}. */
export function formatDateTime(iso: string | null | undefined, locale = 'en-GB'): string {
  if (iso === null || iso === undefined || iso === '') {
    return EM_DASH;
  }
  const d = parseUtcInstant(iso);
  if (Number.isNaN(d.getTime())) {
    return iso;
  }
  return d.toLocaleString(locale, {
    day: '2-digit',
    month: 'short',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    timeZone: DISPLAY_TIME_ZONE,
  });
}

/** Formats a byte count as e.g. "1.2 MB" / "340 KB" / "512 B". Em dash for nullish/NaN/negative. */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || Number.isNaN(bytes) || bytes < 0) {
    return EM_DASH;
  }
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unitIndex = 0;
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024;
    unitIndex += 1;
  }
  return `${value.toFixed(1)} ${units[unitIndex]}`;
}

/**
 * Splits a PascalCase enum-ish value into words for display, e.g. `'ArchivedByOwner'` ->
 * `'Archived By Owner'`. Add any product acronym to `ACRONYMS` so it is not title-cased into
 * something that reads like a typo.
 */
const ACRONYMS = new Set<string>([]);

export function humanizePascalCase(value: string): string {
  return value
    .replace(/(?<!^)([A-Z])/g, ' $1')
    .split(' ')
    .map((word) => (ACRONYMS.has(word.toUpperCase()) ? word.toUpperCase() : word))
    .join(' ');
}

/** Uppercase initials (max 2) for an avatar chip, from a name or email. */
export function initialsOf(nameOrEmail: string): string {
  const parts = nameOrEmail.split(/[\s@.]+/).filter(Boolean);
  const first = parts[0]?.[0] ?? '';
  const second = parts.length > 1 ? (parts[1]?.[0] ?? '') : '';
  return (first + second).toUpperCase() || '?';
}

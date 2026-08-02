/**
 * Single source of truth for ordering the options shown in every dropdown. Sorting is
 * DISPLAY-ONLY: it reorders the option list a user sees and never touches the underlying values
 * (ids, codes, status keys, …) or any server-side ordering.
 *
 * `alpha` (default `true`) sorts A–Z by the visible label, case-insensitive and locale-aware
 * (`localeCompare` with `sensitivity: 'base'` so casing/accents don't split the order, and
 * `numeric: true` so any leading numbers sort naturally: "2 · …" before "10 · …"). Set
 * `alpha: false` to keep the caller's original order — the escape hatch for the two fields with a
 * conventional order (Currency: USD/base first; Main Outline: workflow order), so the client can
 * restore natural order later with a one-word change and no other edits.
 */
export interface SortByLabelOptions {
  /** `true` (default) = A–Z by label; `false` = preserve the input order. */
  alpha?: boolean;
}

export function sortByLabel<T>(
  items: readonly T[],
  getLabel: (item: T) => string,
  options: SortByLabelOptions = {},
): T[] {
  const { alpha = true } = options;
  const copy = [...items];
  if (!alpha) {
    return copy;
  }
  return copy.sort((a, b) =>
    getLabel(a).localeCompare(getLabel(b), undefined, { sensitivity: 'base', numeric: true }),
  );
}

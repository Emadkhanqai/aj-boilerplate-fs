/**
 * The ONE pagination standard (product owner ruling 2026-10-28): PrimeNG paginator below the table
 * only, `Showing 1 to 20 of 859   « ‹ [1] 2 3 › »   Rows [20 ▾]`, default 20, options 10/20/50/100,
 * the rows choice remembered per table. Bind these to every `p-table` that paginates.
 */
export const PAGE_SIZE_OPTIONS: number[] = [10, 20, 50, 100];
export const DEFAULT_PAGE_SIZE = 20;
export const PAGE_REPORT_TEMPLATE = 'Showing {first} to {last} of {totalRecords}';

const keyOf = (table: string): string => `aj.pageSize.${table}`;

/** The remembered rows-per-page for `table`; 20 when nothing valid is stored or storage is blocked. */
export function rememberedPageSize(table: string): number {
  try {
    const n = Number(localStorage.getItem(keyOf(table)));
    return PAGE_SIZE_OPTIONS.includes(n) ? n : DEFAULT_PAGE_SIZE;
  } catch {
    return DEFAULT_PAGE_SIZE;
  }
}

export function rememberPageSize(table: string, rows: number): void {
  try {
    localStorage.setItem(keyOf(table), String(rows));
  } catch {
    // Blocked storage: the choice just is not remembered.
  }
}

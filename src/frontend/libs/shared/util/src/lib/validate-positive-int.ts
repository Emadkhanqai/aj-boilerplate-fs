export interface FieldCheck {
  value: number | null;
  error: string | null;
}

/**
 * Validates a positive-integer form field: rejects blanks, zero, negatives, decimals, and
 * non-numeric input, returning the parsed value or a user-facing message.
 *
 * The `MAX_SAFE_INTEGER` cap matters: `^\d+$` admits arbitrarily long digit strings, and above
 * 2^53 those still parse to an integer `Number` while silently losing precision (or reaching
 * `Infinity`) — so any arithmetic downstream would be wrong while the request still submitted.
 */
export function validatePositiveInt(raw: string): FieldCheck {
  const trimmed = raw.trim();
  if (trimmed === '') {
    return { value: null, error: 'Required.' };
  }
  if (!/^\d+$/.test(trimmed)) {
    return { value: null, error: 'Whole number only.' };
  }
  const parsed = Number(trimmed);
  if (!Number.isInteger(parsed) || parsed < 1) {
    return { value: null, error: 'Must be 1 or more.' };
  }
  if (parsed > Number.MAX_SAFE_INTEGER) {
    return { value: null, error: 'Number is too large.' };
  }
  return { value: parsed, error: null };
}

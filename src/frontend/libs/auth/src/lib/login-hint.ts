const EMAIL_SHAPE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** Whether `value`, trimmed, is a well-formed email address. */
export function isWellFormedEmail(value: string): boolean {
  return EMAIL_SHAPE.test(value.trim());
}

/**
 * The trimmed address to send as `login_hint`, or `null` when there is nothing safe to send.
 *
 * The address is personal data: never log it, never store it. It exists only as a query parameter
 * on the one navigation to the identity provider. A malformed value yields `null` rather than
 * throwing: the hint is a convenience, and the redirect must not fail because of it.
 */
export function loginHintFor(raw: string | undefined): string | null {
  if (raw === undefined) {
    return null;
  }
  const trimmed = raw.trim();
  return isWellFormedEmail(trimmed) ? trimmed : null;
}

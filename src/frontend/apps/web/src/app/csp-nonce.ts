/**
 * The per-request CSP nonce, written into the document by nginx. `index.html` carries
 * `<app-root ngCspNonce="__CSP_NONCE__">` and the gateway's `sub_filter` replaces the placeholder
 * with the same `$request_id` it puts in the `Content-Security-Policy: style-src 'nonce-…'` header,
 * so PrimeNG's runtime-injected `<style>` tags are allowed without `'unsafe-inline'`.
 *
 * When nothing replaced it (`nx serve`, a build behind no gateway) the attribute still holds the
 * placeholder: that is "no nonce", and no CSP is being enforced either.
 */
export const CSP_NONCE_PLACEHOLDER = '__CSP_NONCE__';

export function readCspNonce(doc: Document | undefined = globalThis.document): string | undefined {
  const nonce = doc?.querySelector('app-root')?.getAttribute('ngCspNonce');
  return nonce && nonce !== CSP_NONCE_PLACEHOLDER ? nonce : undefined;
}

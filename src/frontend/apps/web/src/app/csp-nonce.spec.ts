import { describe, expect, it } from 'vitest';
import { CSP_NONCE_PLACEHOLDER, readCspNonce } from './csp-nonce';

function docWith(attr: string | null): Document {
  const doc = document.implementation.createHTMLDocument('t');
  const root = doc.createElement('app-root');
  if (attr !== null) {
    root.setAttribute('ngCspNonce', attr);
  }
  doc.body.appendChild(root);
  return doc;
}

describe('readCspNonce', () => {
  it('returns the nonce nginx stamped into the document', () => {
    expect(readCspNonce(docWith('3f9a1c'))).toBe('3f9a1c');
  });

  it('returns undefined while the placeholder is still there (nx serve, no gateway: no CSP either)', () => {
    expect(readCspNonce(docWith(CSP_NONCE_PLACEHOLDER))).toBeUndefined();
  });

  it('returns undefined when the attribute is absent or empty', () => {
    expect(readCspNonce(docWith(null))).toBeUndefined();
    expect(readCspNonce(docWith(''))).toBeUndefined();
  });

  it('returns undefined when there is no <app-root> at all', () => {
    expect(readCspNonce(document.implementation.createHTMLDocument('t'))).toBeUndefined();
  });
});

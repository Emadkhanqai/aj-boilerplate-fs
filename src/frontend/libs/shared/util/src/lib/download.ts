/** Triggers a browser download of `blob` named `filename`, without navigating away from the
 * page. The anchor must be in the document for the click to register in every browser, hence
 * the append/remove around it; revoking the object URL afterwards is what stops the blob leaking
 * for the lifetime of the page. */
export function downloadBlob(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  document.body.appendChild(anchor);
  anchor.click();
  document.body.removeChild(anchor);
  URL.revokeObjectURL(url);
}

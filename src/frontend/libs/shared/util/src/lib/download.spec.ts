import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { downloadBlob } from './download';

describe('downloadBlob', () => {
  let createObjectURLSpy: ReturnType<typeof vi.fn>;
  let revokeObjectURLSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    createObjectURLSpy = vi.fn(() => 'blob:mock-url');
    revokeObjectURLSpy = vi.fn();
    URL.createObjectURL = createObjectURLSpy as unknown as typeof URL.createObjectURL;
    URL.revokeObjectURL = revokeObjectURLSpy as unknown as typeof URL.revokeObjectURL;
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('creates an object URL, clicks a throwaway anchor with the given filename, then revokes the URL', () => {
    const blob = new Blob(['fake pdf bytes'], { type: 'application/pdf' });
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const bodyChildCountBefore = document.body.childElementCount;

    downloadBlob(blob, 'item-export.pdf');

    expect(createObjectURLSpy).toHaveBeenCalledWith(blob);
    expect(clickSpy).toHaveBeenCalledTimes(1);
    expect(revokeObjectURLSpy).toHaveBeenCalledWith('blob:mock-url');
    expect(document.body.childElementCount).toBe(bodyChildCountBefore);

    clickSpy.mockRestore();
  });

  it('sets the anchor href to the object URL and download attribute to the filename before clicking', () => {
    let capturedHref = '';
    let capturedDownload = '';
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      capturedHref = this.href;
      capturedDownload = this.download;
    });

    downloadBlob(new Blob(['x']), 'report.xlsx');

    expect(capturedHref).toBe('blob:mock-url');
    expect(capturedDownload).toBe('report.xlsx');
    clickSpy.mockRestore();
  });
});

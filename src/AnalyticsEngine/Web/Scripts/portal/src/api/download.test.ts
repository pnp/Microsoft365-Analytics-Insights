import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { filenameFromResponse, REVOKE_DELAY_MS, saveBlob } from './download';

function withDisposition(value: string | null): Response {
  return new Response('', { headers: value === null ? {} : { 'Content-Disposition': value } });
}

describe('saveBlob', () => {
  const createObjectURL = vi.fn(() => 'blob:contoso-export');
  const revokeObjectURL = vi.fn();

  beforeEach(() => {
    vi.useFakeTimers();
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectURL });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectURL });
    createObjectURL.mockClear();
    revokeObjectURL.mockClear();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('keeps the object URL alive after the click, for a browser that reads the blob later', () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    saveBlob(new Blob(['Contoso,1\r\n'], { type: 'text/csv' }), 'contoso.csv');

    expect(click).toHaveBeenCalledOnce();
    expect(revokeObjectURL).not.toHaveBeenCalled();
    expect(document.querySelector('a[download]')).toBeNull();

    vi.advanceTimersByTime(REVOKE_DELAY_MS);
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:contoso-export');
  });
});

describe('filenameFromResponse', () => {
  it('prefers the UTF-8 name, so a non-Latin file name survives', () => {
    const header = "attachment; filename=\"fallback.csv\"; filename*=UTF-8''Contoso%20%CE%9A%CE%B1%CE%BB%CE%B7%CE%BC%CE%AD%CF%81%CE%B1.csv";
    expect(filenameFromResponse(withDisposition(header))).toBe('Contoso Καλημέρα.csv');
  });

  it('falls back to the plain name when the UTF-8 one is malformed, instead of failing the download', () => {
    const header = "attachment; filename*=UTF-8''contoso%E0%A4%A.csv; filename=\"contoso.csv\"";
    expect(filenameFromResponse(withDisposition(header))).toBe('contoso.csv');
  });

  it('reads a plain name, and returns null when there is none', () => {
    expect(filenameFromResponse(withDisposition('attachment; filename="contoso export.csv"'))).toBe('contoso export.csv');
    expect(filenameFromResponse(withDisposition("attachment; filename*=UTF-8''contoso%E0%A4%A.csv"))).toBeNull();
    expect(filenameFromResponse(withDisposition('attachment'))).toBeNull();
    expect(filenameFromResponse(withDisposition(null))).toBeNull();
  });
});

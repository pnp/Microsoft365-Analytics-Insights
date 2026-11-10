/**
 * Hands data the portal already holds to the browser as a file - the one implementation every
 * export uses, so a fix to how downloads behave reaches all of them.
 */

/**
 * How long an object URL outlives the click that started its download.
 *
 * `click()` only starts the download. Some browsers - Safari is the usual one - read the blob after
 * the event has returned, and revoking the URL straight away fails that download with nothing on
 * screen. A minute is far longer than any read needs, and the blob is freed when it elapses.
 */
export const REVOKE_DELAY_MS = 60_000;

/** Saves a blob to disk via a transient object URL, without navigating away. */
export function saveBlob(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  try {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = filename;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    setTimeout(() => URL.revokeObjectURL(url), REVOKE_DELAY_MS);
  }
}

/**
 * The file name a response asks to be saved as, from its `Content-Disposition`, or null.
 *
 * RFC 6266's `filename*` (percent-encoded UTF-8, so a non-Latin name survives) wins over the plain
 * `filename`. A malformed escape in it makes `decodeURIComponent` throw, which used to fail a
 * download that had already succeeded over its name alone; the plain name, or the caller's
 * default, is used instead.
 */
export function filenameFromResponse(response: Response): string | null {
  const header = response.headers.get('Content-Disposition');
  if (!header) return null;

  const encoded = /filename\*\s*=\s*UTF-8''([^;]+)/i.exec(header)?.[1]?.trim();
  if (encoded) {
    try {
      return decodeURIComponent(encoded);
    } catch {
      // Malformed; fall back to the plain name below.
    }
  }

  return /filename\s*=\s*"?([^";]+)"?/i.exec(header)?.[1] ?? null;
}

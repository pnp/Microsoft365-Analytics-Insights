import { describe, it, expect } from 'vitest';
import { ACTIVITY_BANDS, bandColour, bandForeground } from './bands';

// WCAG 2.x relative luminance + contrast ratio, so the badge fill/foreground pairs are checked with
// the same maths the reviewer used (white on low #c19c00 = 2.62, on unknown #8a8886 = 3.53 - both fail).
function channelLuminance(value8bit: number): number {
  const c = value8bit / 255;
  return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
}

function relativeLuminance(hex: string): number {
  const h = hex.replace('#', '');
  const r = parseInt(h.slice(0, 2), 16);
  const g = parseInt(h.slice(2, 4), 16);
  const b = parseInt(h.slice(4, 6), 16);
  return 0.2126 * channelLuminance(r) + 0.7152 * channelLuminance(g) + 0.0722 * channelLuminance(b);
}

function contrastRatio(a: string, b: string): number {
  const la = relativeLuminance(a);
  const lb = relativeLuminance(b);
  const lighter = Math.max(la, lb);
  const darker = Math.min(la, lb);
  return (lighter + 0.05) / (darker + 0.05);
}

const AA_NORMAL = 4.5;

describe('band badge contrast (WCAG AA)', () => {
  it('every band badge foreground clears 4.5:1 against its fill', () => {
    for (const band of ACTIVITY_BANDS) {
      const ratio = contrastRatio(band.colour, band.foreground);
      expect(
        ratio,
        `${band.key}: ${band.foreground} on ${band.colour} = ${ratio.toFixed(2)}`,
      ).toBeGreaterThanOrEqual(AA_NORMAL);
    }
  });

  it('the two light fills (low, unknown) use dark text - white would fail, which was the bug', () => {
    for (const key of ['low', 'unknown'] as const) {
      // The original white-on-fill choice fails AA...
      expect(contrastRatio(bandColour(key), '#ffffff')).toBeLessThan(AA_NORMAL);
      // ...and the chosen foreground is dark (not white) and passes.
      expect(bandForeground(key)).not.toBe('#ffffff');
      expect(contrastRatio(bandColour(key), bandForeground(key))).toBeGreaterThanOrEqual(AA_NORMAL);
    }
  });

  it('leaves the moderate fill untouched (it already passed with white)', () => {
    expect(bandColour('moderate')).toBe('#498205');
    expect(bandForeground('moderate')).toBe('#ffffff');
    expect(contrastRatio('#498205', '#ffffff')).toBeGreaterThanOrEqual(AA_NORMAL);
  });
});

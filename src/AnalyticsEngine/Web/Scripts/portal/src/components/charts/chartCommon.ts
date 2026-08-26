// Shared helpers for the lightweight SVG report charts. Kept dependency-free (no charting library)
// so the portal stays small and there is nothing extra to deploy.

/**
 * Categorical palette for chart series / bars. Saturated Fluent-family colours that read well on
 * the app's light theme (webLightTheme). Series cycle through these in order.
 */
export const CHART_PALETTE = [
  '#0f6cbd', // brand blue
  '#d13438', // red
  '#107c10', // green
  '#8764b8', // purple
  '#ca5010', // orange
  '#008272', // teal
  '#c19c00', // gold
  '#5c2e91', // dark purple
] as const;

/**
 * A lighter partner for each palette colour, for gradient fills.
 *
 * Flat fills read as functional; a subtle vertical or horizontal gradient reads as finished, which
 * matters when the same chart ends up in a board pack. The lighter stop is only ever used as the
 * far end of a gradient on the same hue, so it never changes which series a colour identifies.
 */
export const CHART_PALETTE_LIGHT = [
  '#63a7e0',
  '#e57377',
  '#5fbc5f',
  '#b7a1d8',
  '#e8a173',
  '#4fb8ab',
  '#dfc65a',
  '#9c7bc4',
] as const;

/** Colour for the series/bar at index i (cycles through the palette). */
export function seriesColor(i: number): string {
  return CHART_PALETTE[i % CHART_PALETTE.length];
}

/** The lighter partner of {@link seriesColor}, for the far end of a gradient fill. */
export function seriesColorLight(i: number): string {
  return CHART_PALETTE_LIGHT[i % CHART_PALETTE_LIGHT.length];
}

/**
 * Compact number for axis labels: 1234 -> "1.2k", 2_500_000 -> "2.5M".
 *
 * Small fractional values keep their decimals. Rounding them to whole numbers turned a sentiment
 * axis of 0, 0.2, 0.4, 0.6, 0.8, 1 into "0, 0, 0, 1, 1, 1" - six labels, four of them duplicates and
 * none of them true. Integer inputs are unaffected, so the counting charts render exactly as before.
 */
export function formatCompact(n: number): string {
  const abs = Math.abs(n);
  if (abs >= 1e9) return `${trim(n / 1e9)}B`;
  if (abs >= 1e6) return `${trim(n / 1e6)}M`;
  if (abs >= 1e3) return `${trim(n / 1e3)}k`;
  if (Number.isInteger(n)) return String(n);

  // Enough precision to keep neighbouring ticks distinct without a wall of digits.
  return Number(n.toFixed(2)).toString();
}

function trim(n: number): string {
  // One decimal place, but drop a trailing ".0".
  return n.toFixed(1).replace(/\.0$/, '');
}

/** Full number for tooltips (e.g. "1,234"; fractional values keep up to two decimals). */
export function formatValue(n: number): string {
  const rounded = Number.isInteger(n) ? n : Math.round(n * 100) / 100;
  return rounded.toLocaleString();
}

/** Week-start ISO date -> short label like "14 Apr". */
export function formatWeek(iso: string): string {
  // The week starts are UTC date-only values (Kind=Utc, serialised with a trailing Z), so format
  // them in UTC - otherwise a Monday renders as the previous Sunday for viewers west of UTC.
  return new Date(iso).toLocaleDateString(undefined, { day: 'numeric', month: 'short', timeZone: 'UTC' });
}

/** Week-start ISO date -> longer label like "Mon 14 Apr 2026" (tooltip header). */
export function formatWeekLong(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    timeZone: 'UTC',
  });
}

/**
 * "Nice" y-axis maximum and evenly-spaced ticks (including 0) for a given data max, so the axis
 * ends on a round number. Always returns at least [0, 1] so an all-zero chart still renders.
 */
export function niceTicks(dataMax: number, targetTicks = 4): { max: number; ticks: number[] } {
  if (!Number.isFinite(dataMax) || dataMax <= 0) {
    return { max: 1, ticks: [0, 1] };
  }

  const step = niceNum(dataMax / targetTicks, true);
  const max = Math.ceil(dataMax / step) * step;
  const ticks: number[] = [];
  for (let v = 0; v <= max + step / 2; v += step) {
    ticks.push(Math.round(v * 1e6) / 1e6);
  }
  return { max, ticks };
}

/** Rounds a range to a "nice" 1/2/5 * 10^n value. */
function niceNum(range: number, round: boolean): number {
  const exp = Math.floor(Math.log10(range));
  const frac = range / Math.pow(10, exp);
  let nice: number;
  if (round) {
    if (frac < 1.5) nice = 1;
    else if (frac < 3) nice = 2;
    else if (frac < 7) nice = 5;
    else nice = 10;
  } else {
    if (frac <= 1) nice = 1;
    else if (frac <= 2) nice = 2;
    else if (frac <= 5) nice = 5;
    else nice = 10;
  }
  return nice * Math.pow(10, exp);
}

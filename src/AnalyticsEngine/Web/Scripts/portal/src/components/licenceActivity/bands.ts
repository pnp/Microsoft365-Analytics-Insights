import type { LicenceActivityDistribution } from '../../types/licenceActivity';

/** The engagement bands, worst-to-best after the two "active" tiers, with the two non-active tiers
 * kept visually distinct: `zero` (measured, no activity) is red; `unknown` (not measured) is grey. */
export type BandKey = 'high' | 'moderate' | 'low' | 'zero' | 'unknown';

export interface BandDef {
  key: BandKey;
  label: string;
  colour: string;
  /** The badge text colour to use ON `colour`, chosen so the pair clears WCAG AA (>= 4.5:1). The two
   *  light fills (low gold, unknown grey) need dark text; the darker fills read fine against white. */
  foreground: string;
}

// Foregrounds are deliberately per-band rather than a single "on brand" white: white on the low
// (#c19c00, ratio 2.62) and unknown (#8a8886, ratio 3.53) fills fails AA, so those two use near-black
// (ratios ~8.0 and ~5.9). High/Moderate/Zero keep white (ratios ~6.8 / 4.69 / 4.93). See bands.test.ts.
const DARK_FG = '#000000';
const LIGHT_FG = '#ffffff';

export const ACTIVITY_BANDS: BandDef[] = [
  { key: 'high', label: 'High', colour: '#0b6a0b', foreground: LIGHT_FG },
  { key: 'moderate', label: 'Moderate', colour: '#498205', foreground: LIGHT_FG },
  { key: 'low', label: 'Low', colour: '#c19c00', foreground: DARK_FG },
  { key: 'zero', label: 'No activity', colour: '#d13438', foreground: LIGHT_FG },
  { key: 'unknown', label: 'Unknown', colour: '#8a8886', foreground: DARK_FG },
];

const BY_KEY: Record<string, BandDef> = Object.fromEntries(ACTIVITY_BANDS.map((b) => [b.key, b]));

/**
 * The official activity-level definitions, mirroring LicenceActivityRules.Method on the backend. They
 * describe how often someone was active across the READINGS taken in the period - deliberately not
 * "active days", which the figures only become if a service's source says so.
 */
export const BAND_DESCRIPTIONS: Record<BandKey, string> = {
  high: 'Active in three quarters or more of the weeks that were measured.',
  moderate: 'Active in a quarter to under three quarters of the weeks that were measured.',
  low: 'Active in under a quarter of the weeks that were measured, but active in at least one.',
  zero: 'Complete reporting data shows no activity in any week for this user and period. Every week was measured in full.',
  unknown:
    'At least one week could not be measured in full, so there is not enough reporting data to determine activity ' +
    'for this user and period. Reports or user rows may be missing, coverage may be incomplete, or usage counters ' +
    'may be unavailable. This is not evidence of no activity.',
};

export const COPILOT_COVERAGE_NOTE =
  'The official Copilot usage report covers Copilot-licensed users only. These charts can also include people with ' +
  'other licences, so someone without a Copilot licence may appear as Unknown rather than inactive. ' +
  'Unknown alone does not tell you whether someone has a Copilot licence.';

/** A one-line summary of how the levels are worked out, for a column tooltip. */
export const BAND_METHOD =
  "Activity levels describe how many of the period's weeks someone was active in: " +
  'High = three quarters or more, Moderate = a quarter to under three quarters, Low = under a quarter, ' +
  'No activity = none. A week is only counted when every one of its days was imported; where a week could ' +
  'not be measured in full the level is Unknown, not zero.';

export function bandLabel(band: string): string {
  return BY_KEY[band]?.label ?? band;
}

export function bandDescription(band: string): string | null {
  return (BY_KEY[band] ? BAND_DESCRIPTIONS[band as BandKey] : null) ?? null;
}

export function bandColour(band: string): string {
  return BY_KEY[band]?.colour ?? '#8a8886';
}

/** The accessible badge text colour for a band's fill (see BandDef.foreground). Defaults to the
 *  unknown band's dark foreground for any unrecognised band, matching bandColour's grey fallback. */
export function bandForeground(band: string): string {
  return BY_KEY[band]?.foreground ?? BY_KEY.unknown.foreground;
}

/** The active-sample frequency as a percentage, or null when nothing was observed. */
export function frequencyPct(activeSamples: number, observedSamples: number): number | null {
  return observedSamples > 0 ? (activeSamples / observedSamples) * 100 : null;
}

export function bandCount(distribution: LicenceActivityDistribution, key: BandKey): number {
  return distribution[key];
}

/** Everyone the distribution accounts for, measured or not. */
export function distributionTotal(distribution: LicenceActivityDistribution): number {
  return distribution.high + distribution.moderate + distribution.low + distribution.zero + distribution.unknown;
}

/** Users with any measured activity (high + moderate + low). */
export function activeCount(distribution: LicenceActivityDistribution): number {
  return distribution.high + distribution.moderate + distribution.low;
}

/** Users measured at all (everyone except the unknown tier). */
export function measuredCount(distribution: LicenceActivityDistribution): number {
  return distributionTotal(distribution) - distribution.unknown;
}

/**
 * Active users as a percentage of those actually MEASURED, or null when nobody was measured.
 *
 * Deliberately divides by the measured population, not the assigned population: including the
 * unknown tier in the denominator would understate the rate and, worse, let "we didn't measure it"
 * masquerade as "they weren't active".
 */
export function activeRatePct(distribution: LicenceActivityDistribution): number | null {
  const measured = measuredCount(distribution);
  return measured <= 0 ? null : (activeCount(distribution) / measured) * 100;
}

import type {
  LicenceActivityDistribution,
  LicenceActivityOverview,
  LicenceActivitySku,
  WorkloadKey,
} from '../../types/licenceActivity';
import { activeRatePct } from './bands';

/**
 * Which population a view of the report describes: everyone holding a licence (`'all'`, each person
 * counted once) or the holders of one licence, by its licence type id.
 *
 * Everyone is the default. It answers "how much is each service used?" without having to pick a
 * licence first, and it is the baseline each licence is compared with.
 */
export type LicenceScope = 'all' | number;

export const ALL_LICENCES = 'all' as const;

/** The licence a scope names, or null for everyone (or a licence no longer in the figures). */
export function scopeLicence(overview: LicenceActivityOverview, scope: LicenceScope): LicenceActivitySku | null {
  return scope === ALL_LICENCES ? null : overview.licences.find((l) => l.licenceTypeId === scope) ?? null;
}

/** The people a scope covers. */
export function scopeAssignedUsers(overview: LicenceActivityOverview, scope: LicenceScope): number {
  const licence = scopeLicence(overview, scope);
  if (licence) return licence.assignedUsers;
  return overview.allLicences?.assignedUsers ?? overview.distinctAssignedUsers;
}

/** The five service distributions for a scope. Empty when an older server sent no all-licence figures. */
export function scopeWorkloads(overview: LicenceActivityOverview, scope: LicenceScope): LicenceActivityDistribution[] {
  const licence = scopeLicence(overview, scope);
  if (licence) return licence.workloads;
  return scope === ALL_LICENCES ? overview.allLicences?.workloads ?? [] : [];
}

/** A scope's adoption score, or null when it could not be measured (or the server did not send one). */
export function scopeScore(overview: LicenceActivityOverview, scope: LicenceScope): number | null {
  const licence = scopeLicence(overview, scope);
  if (licence) return licence.adoptionScore ?? null;
  return scope === ALL_LICENCES ? overview.allLicences?.adoptionScore ?? null : null;
}

/** One service's distribution out of a list, or null. */
export function distributionFor(
  workloads: LicenceActivityDistribution[],
  workload: WorkloadKey | string,
): LicenceActivityDistribution | null {
  return workloads.find((d) => d.workload === workload) ?? null;
}

/** A position in a ranking: 1 is the highest. Ties share a position ("1, 2, 2, 4"). */
export interface Rank {
  rank: number;
  of: number;
}

function rankAmong(values: (number | null)[], value: number | null): Rank | null {
  if (value == null) return null;
  const ranked = values.filter((v): v is number => v != null);
  return { rank: 1 + ranked.filter((v) => v > value).length, of: ranked.length };
}

/**
 * Where one licence sits among the licences somebody holds, by the share of its measured holders who
 * were active in a service. Null when that licence's service could not be measured, or nobody holds it.
 */
export function workloadRank(
  licences: LicenceActivitySku[],
  licenceTypeId: number,
  workload: WorkloadKey | string,
): Rank | null {
  const held = licences.filter((l) => l.assignedUsers > 0);
  const rate = (l: LicenceActivitySku) => {
    const d = distributionFor(l.workloads, workload);
    return d ? activeRatePct(d) : null;
  };
  const self = held.find((l) => l.licenceTypeId === licenceTypeId);
  return self ? rankAmong(held.map(rate), rate(self)) : null;
}

/** Where one licence sits among the licences somebody holds, by adoption score. */
export function scoreRank(licences: LicenceActivitySku[], licenceTypeId: number): Rank | null {
  const held = licences.filter((l) => l.assignedUsers > 0);
  const self = held.find((l) => l.licenceTypeId === licenceTypeId);
  return self ? rankAmong(held.map((l) => l.adoptionScore ?? null), self.adoptionScore ?? null) : null;
}

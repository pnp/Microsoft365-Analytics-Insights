import { afterEach, beforeAll, describe, expect, it } from 'vitest';

import { loadCatalog } from '../../i18n/catalog';
import { setActiveLanguage, translateStatic, type TFunction } from '../../i18n';
import { AdoptionBand, type CopilotAdoptionOptions, type CoworkReadinessRow, type LicensedUserAdoptionRow } from '../../types/copilotAdoption';
import {
  coworkRationaleText,
  coworkTierLabel,
  recommendedActionText,
  reclaimEligibilityReason,
} from './serverText';

const tEs: TFunction = (key, values) => translateStatic('es', key, values);
const tEn: TFunction = (key, values) => translateStatic('en', key, values);

const OPTIONS = {
  historyDays: 365,
  reclaimGraceDays: 30,
  coworkFluencyMinScore: 50,
  coworkRegularMinActiveDays: 3,
} as CopilotAdoptionOptions;

function licensed(overrides: Partial<LicensedUserAdoptionRow>): LicensedUserAdoptionRow {
  return {
    band: AdoptionBand.NeverUsed,
    reclaimEligibility: 'review',
    reclaimExclusionReason: null,
    accountEnabled: true,
    tooNewToJudge: false,
    daysSinceTenureStart: null,
    tenureBasis: 'unknown',
    reclaimExclusionReviewAfterUtc: null,
    daysSinceLastUse: null,
    breadthScore: 0,
    appsUsed: 0,
    recommendedAction: 'server fallback',
    ...overrides,
  } as LicensedUserAdoptionRow;
}

function cowork(overrides: Partial<CoworkReadinessRow>): CoworkReadinessRow {
  return {
    tier: 'primeCandidate',
    tierLabel: 'Prime candidate',
    rationale: 'server fallback',
    coworkInteractions: 0,
    coworkActiveDays: 0,
    coworkReportTotalTasks: null,
    coworkReportActiveDays: null,
    fluencyScore: 70,
    coordinationLoadScore: 80,
    teamsMeetings: 0,
    teamsMessages: 0,
    emailsSent: 0,
    emailsRead: 0,
    filesViewedOrEdited: 0,
    ...overrides,
  } as CoworkReadinessRow;
}

describe('Copilot Adoption server-authored text reproduction', () => {
  beforeAll(async () => {
    await loadCatalog('es');
  });

  afterEach(() => setActiveLanguage('en'));

  it('keeps unknown tenure unknown rather than treating it as zero-tenure account age', () => {
    setActiveLanguage('es');
    expect(reclaimEligibilityReason(tEs, licensed({}), OPTIONS)).toBe(
      'No se observa uso de Copilot, pero se desconoce el estado de cuenta o la antigüedad.',
    );
  });

  it('translates the tenure-basis code before interpolating it into reclaim prose', () => {
    setActiveLanguage('es');
    expect(reclaimEligibilityReason(
      tEs,
      licensed({ tooNewToJudge: true, tenureBasis: 'accountAge', daysSinceTenureStart: 4 }),
      OPTIONS,
    )).toBe('Demasiado nuevo para evaluarlo: la antigüedad de la cuenta está por debajo del periodo de gracia de 30 días.');
    expect(recommendedActionText(
      tEs,
      licensed({ tooNewToJudge: true, tenureBasis: 'accountAge', daysSinceTenureStart: 4 }),
      OPTIONS,
    )).not.toContain('accountAge');
  });

  it('falls back to an unrecognised tenure code instead of inventing a translation', () => {
    setActiveLanguage('es');
    expect(reclaimEligibilityReason(
      tEs,
      licensed({ tooNewToJudge: true, tenureBasis: 'futureSignal', daysSinceTenureStart: 4 }),
      OPTIONS,
    )).toContain('futureSignal');
  });

  it('translates Cowork tier labels from the stable tier code with a server fallback for unknown tiers', () => {
    setActiveLanguage('es');
    expect(coworkTierLabel(tEs, 'primeCandidate', 'Prime candidate')).toBe('Candidato principal');
    expect(coworkTierLabel(tEs, 'futureTier', 'Future tier')).toBe('Future tier');
  });

  it('reproduces the report-only Cowork rationale without turning a missing task count into zero', () => {
    setActiveLanguage('es');
    const text = coworkRationaleText(
      tEs,
      cowork({ tier: 'established', coworkReportActiveDays: 10, coworkReportTotalTasks: null }),
      OPTIONS,
    );

    expect(text).toBe('Ya consolidado: activo durante 10 días en el informe de uso de Cowork de Microsoft. Manténgalo en el ámbito.');
    expect(text).not.toMatch(/\b0\s+tareas?\b/i);
    expect(text).not.toContain('tareas de Cowork');
  });

  it('reproduces the no-workload Cowork rationale as unknown activity, not zero work', () => {
    setActiveLanguage('es');
    expect(coworkRationaleText(tEs, cowork({ tier: 'notIndicated', fluencyScore: 12, coordinationLoadScore: 9 }), OPTIONS)).toBe(
      'No hay caso con la evidencia actual: fluidez con Copilot 12/100, carga de coordinación 9/100 (sin actividad de Microsoft 365 registrada en este periodo).',
    );
  });

  it('leaves English Cowork rationale byte-for-byte as the server supplied it', () => {
    setActiveLanguage('en');
    expect(coworkRationaleText(tEn, cowork({ rationale: 'Server English exactly.' }), OPTIONS)).toBe('Server English exactly.');
  });
});

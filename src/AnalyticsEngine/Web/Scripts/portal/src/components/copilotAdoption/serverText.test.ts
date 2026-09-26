import { afterEach, beforeAll, describe, expect, it } from 'vitest';

import { loadCatalog } from '../../i18n/catalog';
import { setActiveLanguage, translateStatic, type TFunction } from '../../i18n';
import { AdoptionBand, type CopilotAdoptionOptions, type CopilotAdoptionWarningDetail, type CoworkReadinessRow, type LicensedUserAdoptionRow } from '../../types/copilotAdoption';
import {
  COPILOT_ADOPTION_WARNING_KEYS,
  copilotAdoptionWarningText,
  coworkRationaleText,
  coworkTierLabel,
  isCoworkWarning,
  isLicenceOpportunityWarning,
  recommendedActionText,
  reclaimCaveatText,
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

  it('translates a plain server warning in Spanish', () => {
    setActiveLanguage('es');
    expect(copilotAdoptionWarningText(
      tEs,
      { key: COPILOT_ADOPTION_WARNING_KEYS.NoCopilotData },
      'Neither the Copilot audit import nor Microsoft\'s Copilot usage report has any data for this period, so every licensed user will appear as unused. Check the Health page before acting on these numbers.',
    )).toBe('Ni la importación de auditoría de Copilot ni el informe de uso de Copilot de Microsoft tienen datos para este periodo, por lo que todos los usuarios con licencia aparecerán como sin uso. Compruebe la página Estado antes de actuar sobre estos números.');
  });

  it('formats warning numbers with the active locale', () => {
    setActiveLanguage('es');
    expect(copilotAdoptionWarningText(
      tEs,
      {
        key: COPILOT_ADOPTION_WARNING_KEYS.LicensedUsersSubset,
        values: { licensedUsers: 1234, scoredUsers: 1000 },
      },
      'server fallback',
    )).toContain('1.234 licencias de Copilot');
  });

  it('keeps SKU names verbatim while translating the SKU mismatch warning', () => {
    setActiveLanguage('es');
    const text = copilotAdoptionWarningText(
      tEs,
      {
        key: COPILOT_ADOPTION_WARNING_KEYS.SkuSeatMismatch,
        values: { skuName: 'Contoso Copilot SKU', purchased: 1234, assigned: 1200 },
      },
      'server fallback',
    );

    expect(text).toContain('Contoso Copilot SKU');
    expect(text).toContain('1.234 comprados');
  });

  it('falls back to server English for an unknown warning key', () => {
    setActiveLanguage('es');
    expect(copilotAdoptionWarningText(
      tEs,
      { key: 'futureWarning', values: { count: 1 } },
      'Server fallback warning.',
    )).toBe('Server fallback warning.');
  });

  it('classifies panel warnings by stable key instead of English text', () => {
    const cowork: CopilotAdoptionWarningDetail = { key: COPILOT_ADOPTION_WARNING_KEYS.CoworkM365UsageMissing };
    const opportunity: CopilotAdoptionWarningDetail = { key: COPILOT_ADOPTION_WARNING_KEYS.LicenceCandidatesAuditOnly };

    expect(isCoworkWarning(cowork)).toBe(true);
    expect(isCoworkWarning(opportunity)).toBe(false);
    expect(isLicenceOpportunityWarning(opportunity)).toBe(true);
    expect(isLicenceOpportunityWarning(cowork)).toBe(false);
  });

  it('translates the reclaim caveat from its stable key', () => {
    setActiveLanguage('es');
    expect(reclaimCaveatText(
      tEs,
      'copilotAdoption.server.reclaimCaveat',
      'Reclaim excludes admin exclusions and separates review-only cases. Leave, part-time patterns, service/shared accounts and role-based mailboxes are not detectable from Microsoft 365 usage data.',
    )).toBe('La recuperación excluye las exclusiones administrativas y separa los casos que solo requieren revisión. Las bajas, los patrones de jornada parcial, las cuentas de servicio o compartidas y los buzones basados en roles no se pueden detectar a partir de los datos de uso de Microsoft 365.');
  });
});

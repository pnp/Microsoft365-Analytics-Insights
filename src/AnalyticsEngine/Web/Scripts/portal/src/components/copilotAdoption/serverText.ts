import type {
  AgentUsageRow,
  CopilotAdoptionAvailability,
  CopilotAdoptionOptions,
  CopilotAdoptionWarningDetail,
  CoworkReadinessRow,
  CoworkTier,
  LicenceOpportunityRow,
  LicensedUserAdoptionRow,
} from '../../types/copilotAdoption';
import { AdoptionBand, AgentHealth } from '../../types/copilotAdoption';
import { formatCount } from '../shared/KpiGrid';
import { serverPlaceholderText } from '../shared/serverPlaceholder';
import { activeLocale, formatNumber, type TFunction, type TranslationKey, type TranslationValues } from '../../i18n';

function catalogText(t: TFunction, key: TranslationKey, fallback: string, values?: TranslationValues): string {
  if (activeLocale().startsWith('en')) return fallback;
  const translated = t(key, values);
  return translated === key ? fallback : translated;
}

export const COPILOT_ADOPTION_WARNING_KEYS = {
  NoLicenceInformation: 'noLicenceInformation',
  NoCopilotLicences: 'noCopilotLicences',
  CopilotBackfillPending: 'copilotBackfillPending',
  CopilotUsageReportConcealed: 'copilotUsageReportConcealed',
  NoCopilotData: 'noCopilotData',
  AuditMissingUsingUsageReport: 'auditMissingUsingUsageReport',
  AgentInventoryCapped: 'agentInventoryCapped',
  UnlicensedUsageCapped: 'unlicensedUsageCapped',
  LicensedUsersSubset: 'licensedUsersSubset',
  LicenceOpportunitiesNoSources: 'licenceOpportunitiesNoSources',
  LicenceCandidatesAuditOnly: 'licenceCandidatesAuditOnly',
  CoworkReadinessNoSources: 'coworkReadinessNoSources',
  CoworkM365UsageMissing: 'coworkM365UsageMissing',
  CoworkUsageReportMissing: 'coworkUsageReportMissing',
  CoworkAuditMissing: 'coworkAuditMissing',
  UsageReportSourcedUsers: 'usageReportSourcedUsers',
  UsageReportWindowMismatch: 'usageReportWindowMismatch',
  CoworkEligibilityUnknown: 'coworkEligibilityUnknown',
  PurchasedSeatsUnknown: 'purchasedSeatsUnknown',
  SkuSeatMismatch: 'skuSeatMismatch',
  CoworkFluencyMissingAll: 'coworkFluencyMissingAll',
  CoworkFluencyPartial: 'coworkFluencyPartial',
  CouldNotLoad: 'couldNotLoad',
} as const;

const COWORK_WARNING_KEYS = new Set<string>([
  COPILOT_ADOPTION_WARNING_KEYS.CoworkReadinessNoSources,
  COPILOT_ADOPTION_WARNING_KEYS.CoworkM365UsageMissing,
  COPILOT_ADOPTION_WARNING_KEYS.CoworkUsageReportMissing,
  COPILOT_ADOPTION_WARNING_KEYS.CoworkAuditMissing,
  COPILOT_ADOPTION_WARNING_KEYS.CoworkEligibilityUnknown,
  COPILOT_ADOPTION_WARNING_KEYS.CoworkFluencyMissingAll,
  COPILOT_ADOPTION_WARNING_KEYS.CoworkFluencyPartial,
]);

const OPPORTUNITY_WARNING_KEYS = new Set<string>([
  COPILOT_ADOPTION_WARNING_KEYS.LicenceOpportunitiesNoSources,
  COPILOT_ADOPTION_WARNING_KEYS.LicenceCandidatesAuditOnly,
]);

function warningValues(values?: CopilotAdoptionWarningDetail['values']): TranslationValues {
  const mapped: TranslationValues = {};
  Object.entries(values ?? {}).forEach(([key, value]) => {
    mapped[key] = typeof value === 'number'
      ? formatNumber(value, { maximumFractionDigits: key === 'percentage' ? 1 : 0 })
      : String(value ?? '');
  });
  return mapped;
}

export function copilotAdoptionWarningText(
  t: TFunction,
  detail: CopilotAdoptionWarningDetail | undefined,
  english: string,
): string {
  if (!detail?.key) return english;
  const key = `copilotAdoption.server.warning.${detail.key}` as TranslationKey;
  return catalogText(t, key, english, warningValues(detail.values));
}

export function copilotAdoptionWarningIdentity(detail: CopilotAdoptionWarningDetail | undefined, english: string): string {
  return detail?.key ? `${detail.key}:${JSON.stringify(detail.values ?? {})}` : english;
}

export function isCoworkWarning(detail: CopilotAdoptionWarningDetail | undefined): boolean {
  return !!detail?.key && COWORK_WARNING_KEYS.has(detail.key);
}

export function isLicenceOpportunityWarning(detail: CopilotAdoptionWarningDetail | undefined): boolean {
  return !!detail?.key && OPPORTUNITY_WARNING_KEYS.has(detail.key);
}

export function reclaimCaveatText(t: TFunction, key: string | null | undefined, fallback: string | null | undefined): string {
  if (!key) return fallback ?? '';
  return catalogText(t, key as TranslationKey, fallback ?? '');
}

export function adoptionBandLabel(t: TFunction, band: AdoptionBand | string, fallback: string): string {
  const keyByBand: Record<AdoptionBand, TranslationKey> = {
    [AdoptionBand.NeverUsed]: 'copilotAdoption.server.band.neverUsed',
    [AdoptionBand.Dormant]: 'copilotAdoption.server.band.dormant',
    [AdoptionBand.Trialling]: 'copilotAdoption.server.band.trialling',
    [AdoptionBand.Developing]: 'copilotAdoption.server.band.developing',
    [AdoptionBand.Established]: 'copilotAdoption.server.band.established',
    [AdoptionBand.Champion]: 'copilotAdoption.server.band.champion',
  };
  if (typeof band === 'string') {
    const keyByLabel: Record<string, TranslationKey> = {
      'Never used': 'copilotAdoption.server.band.neverUsed',
      Dormant: 'copilotAdoption.server.band.dormant',
      Trialling: 'copilotAdoption.server.band.trialling',
      Developing: 'copilotAdoption.server.band.developing',
      Established: 'copilotAdoption.server.band.established',
      Champion: 'copilotAdoption.server.band.champion',
    };
    const key = keyByLabel[band];
    return key ? catalogText(t, key, fallback) : fallback;
  }
  return catalogText(t, keyByBand[band], fallback);
}

export function habitBucketLabel(t: TFunction, label: string): string {
  const keyByLabel: Record<string, TranslationKey> = {
    Infrequent: 'copilotAdoption.server.habitBucket.infrequent',
    Moderate: 'copilotAdoption.server.habitBucket.moderate',
    Frequent: 'copilotAdoption.server.habitBucket.frequent',
    Daily: 'copilotAdoption.server.habitBucket.daily',
  };
  const key = keyByLabel[label];
  return key ? catalogText(t, key, label) : label;
}

export function habitBucketRangeLabel(t: TFunction, label: string, options: CopilotAdoptionOptions, fallback: string): string {
  const moderate = Math.round(options.habitModerateMinDays);
  const frequent = Math.round(options.habitFrequentMinDays);
  const daily = Math.round(options.habitDailyMinDays);
  switch (label) {
    case 'Infrequent':
      return t('copilotAdoption.server.habitBucket.infrequent.range', { max: Math.max(1, moderate - 1) });
    case 'Moderate':
      return t('copilotAdoption.server.habitBucket.moderate.range', { min: moderate, max: Math.max(moderate, frequent - 1) });
    case 'Frequent':
      return t('copilotAdoption.server.habitBucket.frequent.range', { min: frequent, max: Math.max(frequent, daily - 1) });
    case 'Daily':
      return t('copilotAdoption.server.habitBucket.daily.range', { min: daily });
    default:
      return fallback;
  }
}

export function funnelStageLabel(t: TFunction, label: string): string {
  const keyByLabel: Record<string, TranslationKey> = {
    Licensed: 'copilotAdoption.server.funnel.licensed',
    [['Ever', 'used', 'Copilot'].join(' ')]: 'copilotAdoption.server.funnel.everUsedCopilot',
    [['Active', 'this', 'period'].join(' ')]: 'copilotAdoption.server.funnel.activeThisPeriod',
    'Habitual users': 'copilotAdoption.server.funnel.habitualUsers',
    Champions: 'copilotAdoption.server.funnel.champions',
  };
  const key = keyByLabel[label];
  return key ? catalogText(t, key, label) : label;
}

export function scoreProfileLabel(t: TFunction, label: string): string {
  const keyByLabel: Record<string, TranslationKey> = {
    [['Typical', 'active', 'user'].join(' ')]: 'copilotAdoption.server.scoreProfile.typicalActiveUser',
    'Your Champions': 'copilotAdoption.server.scoreProfile.yourChampions',
  };
  const key = keyByLabel[label];
  return key ? catalogText(t, key, label) : label;
}

export function concentrationLabel(t: TFunction, label: string): string {
  const keyByLabel: Record<string, TranslationKey> = {
    'Top 10%': 'copilotAdoption.server.concentration.top10',
    'Next 15%': 'copilotAdoption.server.concentration.next15',
    'Next 25%': 'copilotAdoption.server.concentration.next25',
    'Bottom 50%': 'copilotAdoption.server.concentration.bottom50',
  };
  const key = keyByLabel[label];
  return key ? catalogText(t, key, label) : label;
}

export function actionLabel(t: TFunction, code: string, fallback: string): string {
  const key = `copilotAdoption.server.action.${code}.label` as TranslationKey;
  return catalogText(t, key, fallback);
}

export function actionDescription(t: TFunction, code: string, fallback: string, options: CopilotAdoptionOptions): string {
  const key = `copilotAdoption.server.action.${code}.description` as TranslationKey;
  const translated = t(key, {
    historyDays: options.historyDays,
    developingScore: options.developingScore,
    establishedScore: options.establishedScore,
    championScore: options.championScore,
  });
  return translated === key ? fallback : translated;
}

function appsPhrase(t: TFunction, appsUsed: number): string {
  return appsUsed === 1
    ? t('copilotAdoptionUsers.server.apps.single')
    : t('copilotAdoptionUsers.server.apps.multiple', { count: appsUsed });
}

export function reclaimEligibilityLabel(t: TFunction, tier: string | null): string {
  switch (tier) {
    case 'certain':
      return t('copilotAdoptionUsers.server.reclaimEligibility.certain');
    case 'probable':
      return t('copilotAdoptionUsers.server.reclaimEligibility.probable');
    case 'review':
      return t('copilotAdoptionUsers.server.reclaimEligibility.review');
    case 'excluded':
      return t('copilotAdoptionUsers.server.reclaimEligibility.excluded');
    default:
      return tier || '—';
  }
}

export const TENURE_BASIS_LABEL_KEYS = {
  accountAge: 'copilotAdoptionUsers.server.tenureBasis.accountAge',
  unknown: 'copilotAdoptionUsers.server.tenureBasis.unknown',
} as const satisfies Record<string, TranslationKey>;

function tenureBasisLabel(t: TFunction, basis: string | null): string {
  if (!basis) return '';
  const key = TENURE_BASIS_LABEL_KEYS[basis as keyof typeof TENURE_BASIS_LABEL_KEYS];
  return key ? catalogText(t, key, basis) : basis;
}

export function reclaimEligibilityReason(t: TFunction, row: LicensedUserAdoptionRow, options: CopilotAdoptionOptions): string {
  if (row.reclaimEligibility === 'excluded' && row.reclaimExclusionReason) {
    return t('copilotAdoptionUsers.server.reclaimReason.excluded', { reason: serverPlaceholderText(t, row.reclaimExclusionReason) });
  }
  if (row.accountEnabled === false) {
    return t('copilotAdoptionUsers.server.reclaimReason.disabled');
  }
  if (row.band === AdoptionBand.NeverUsed) {
    if (row.tooNewToJudge) {
      return t('copilotAdoptionUsers.server.reclaimReason.tooNew', {
        tenureBasis: tenureBasisLabel(t, row.tenureBasis),
        days: options.reclaimGraceDays,
      });
    }
    if (row.accountEnabled === true && row.daysSinceTenureStart !== null) {
      return t('copilotAdoptionUsers.server.reclaimReason.probable', {
        tenureBasis: tenureBasisLabel(t, row.tenureBasis),
        days: options.reclaimGraceDays,
      });
    }
    return t('copilotAdoptionUsers.server.reclaimReason.unknown');
  }
  if (row.band === AdoptionBand.Dormant) {
    return t('copilotAdoptionUsers.server.reclaimReason.dormant');
  }
  return '';
}

export function recommendedActionText(t: TFunction, row: LicensedUserAdoptionRow, options: CopilotAdoptionOptions): string {
  if (row.reclaimEligibility === 'excluded') {
    return row.reclaimExclusionReviewAfterUtc
      ? t('copilotAdoptionUsers.server.recommendedAction.excluded.reviewAfter', {
          reason: serverPlaceholderText(t, row.reclaimExclusionReason) ?? '',
          date: row.reclaimExclusionReviewAfterUtc.slice(0, 10),
        })
      : t('copilotAdoptionUsers.server.recommendedAction.excluded.permanent', { reason: serverPlaceholderText(t, row.reclaimExclusionReason) ?? '' });
  }
  if (row.accountEnabled === false) {
    return t('copilotAdoptionUsers.server.recommendedAction.disabled');
  }
  if (row.tooNewToJudge && row.band === AdoptionBand.NeverUsed) {
    return t('copilotAdoptionUsers.server.recommendedAction.tooNew', {
      tenureBasis: tenureBasisLabel(t, row.tenureBasis),
      days: options.reclaimGraceDays,
    });
  }
  if (row.reclaimEligibility === 'review' && row.band === AdoptionBand.NeverUsed) {
    return t('copilotAdoptionUsers.server.recommendedAction.reviewUnknown');
  }

  switch (row.band) {
    case AdoptionBand.NeverUsed:
      return t('copilotAdoptionUsers.server.recommendedAction.neverUsed', { historyDays: options.historyDays });
    case AdoptionBand.Dormant:
      return t('copilotAdoptionUsers.server.recommendedAction.dormant', {
        since: row.daysSinceLastUse !== null
          ? t('copilotAdoptionUsers.server.recommendedAction.dormant.daysAgo', { days: row.daysSinceLastUse })
          : t('copilotAdoptionUsers.server.recommendedAction.dormant.hasUsedPast'),
      });
    case AdoptionBand.Trialling:
      return t('copilotAdoptionUsers.server.recommendedAction.trialling');
    case AdoptionBand.Developing:
      return row.breadthScore < 34
        ? t('copilotAdoptionUsers.server.recommendedAction.developing.broaden', { apps: appsPhrase(t, row.appsUsed) })
        : t('copilotAdoptionUsers.server.recommendedAction.developing.grow');
    case AdoptionBand.Established:
      return row.breadthScore < 50
        ? t('copilotAdoptionUsers.server.recommendedAction.established.broaden', { apps: appsPhrase(t, row.appsUsed) })
        : t('copilotAdoptionUsers.server.recommendedAction.established.sustain');
    case AdoptionBand.Champion:
      return row.breadthScore < 50
        ? t('copilotAdoptionUsers.server.recommendedAction.champion.broaden', { apps: appsPhrase(t, row.appsUsed) })
        : t('copilotAdoptionUsers.server.recommendedAction.champion.advocate');
    default:
      return row.recommendedAction;
  }
}

export function opportunityTierLabel(t: TFunction, tier: string | null, fallback: string | null): string {
  switch (tier) {
    case 'provenDemand':
      return t('copilotAdoptionUsers.server.opportunityTier.provenDemand');
    case 'workloadInferred':
      return t('copilotAdoptionUsers.server.opportunityTier.workloadInferred');
    case 'none':
      return t('copilotAdoptionUsers.server.opportunityTier.none');
    default:
      return fallback || '';
  }
}

export function opportunityRationale(t: TFunction, row: LicenceOpportunityRow): string {
  const reasons: string[] = [];
  if (row.unlicensedCopilotInteractions > 0) {
    reasons.push(t(
      row.unlicensedCopilotInteractions === 1
        ? 'copilotAdoptionUsers.server.opportunityRationale.copilot.oneInteraction'
        : 'copilotAdoptionUsers.server.opportunityRationale.copilot.otherInteractions',
      {
        interactions: formatCount(row.unlicensedCopilotInteractions),
        days: formatCount(row.unlicensedCopilotActiveDays),
        dayWord: row.unlicensedCopilotActiveDays === 1
          ? t('copilotAdoptionUsers.server.opportunityRationale.day')
          : t('copilotAdoptionUsers.server.opportunityRationale.days'),
      },
    ));
  }
  if (row.teamsMessages + row.teamsMeetings > 0) {
    reasons.push(t('copilotAdoptionUsers.server.opportunityRationale.teams', {
      messages: formatCount(row.teamsMessages),
      meetings: formatCount(row.teamsMeetings),
    }));
  }
  if (row.emailsSent + row.emailsRead > 0) {
    reasons.push(t('copilotAdoptionUsers.server.opportunityRationale.email', {
      sent: formatCount(row.emailsSent),
      read: formatCount(row.emailsRead),
    }));
  }
  if (row.filesViewedOrEdited > 0) {
    reasons.push(t('copilotAdoptionUsers.server.opportunityRationale.files', {
      files: formatCount(row.filesViewedOrEdited),
    }));
  }
  if (reasons.length === 0) return t('copilotAdoptionUsers.server.opportunityRationale.none');

  const reasonText = reasons.join('; ');
  if (row.qualificationTier === 'provenDemand') {
    return t('copilotAdoptionUsers.server.opportunityRationale.provenDemand', { reasons: reasonText });
  }
  if (row.qualificationTier === 'workloadInferred') {
    return t('copilotAdoptionUsers.server.opportunityRationale.workloadInferred', { reasons: reasonText });
  }
  return t('copilotAdoptionUsers.server.opportunityRationale.notRecommended', { reasons: reasonText });
}

export function agentHealthReason(t: TFunction, row: AgentUsageRow, options: CopilotAdoptionOptions): string {
  switch (row.health) {
    case AgentHealth.New:
      return t('copilotAdoptionAgents.server.healthReason.new', { days: options.agentNewDays });
    case AgentHealth.Retire:
      return row.daysSinceLastUse !== null
        ? t('copilotAdoptionAgents.server.healthReason.retire.withDays', {
            days: row.daysSinceLastUse,
            retireDays: options.agentRetireInactiveDays,
          })
        : t('copilotAdoptionAgents.server.healthReason.retire.noUse');
    case AgentHealth.Review:
      return row.daysSinceLastUse !== null && row.daysSinceLastUse >= options.agentReviewInactiveDays
        ? t('copilotAdoptionAgents.server.healthReason.review.quiet', { days: row.daysSinceLastUse })
        : t('copilotAdoptionAgents.server.healthReason.review.fewUsers', {
            users: row.users,
            people: row.users === 1
              ? t('copilotAdoptionAgents.server.healthReason.person')
              : t('copilotAdoptionAgents.server.healthReason.people'),
            minUsers: options.agentMinUsers,
          });
    case AgentHealth.Keep:
      return t('copilotAdoptionAgents.server.healthReason.keep', {
        reviewDays: options.agentReviewInactiveDays,
        users: row.users,
      });
    default:
      return row.healthReason;
  }
}

export const COWORK_TIER_LABEL_KEYS = {
  established: 'copilotAdoptionCowork.tier.established.label',
  trialling: 'copilotAdoptionCowork.tier.trialling.label',
  primeCandidate: 'copilotAdoptionCowork.tier.primeCandidate.label',
  buildFluencyFirst: 'copilotAdoptionCowork.tier.buildFluencyFirst.label',
  lowCoordinationLoad: 'copilotAdoptionCowork.tier.lowCoordinationLoad.label',
  notIndicated: 'copilotAdoptionCowork.tier.notIndicated.label',
} as const satisfies Record<CoworkTier, TranslationKey>;

export function coworkTierLabel(t: TFunction, tier: string, fallback: string): string {
  const key = COWORK_TIER_LABEL_KEYS[tier as CoworkTier];
  return key ? catalogText(t, key, fallback) : fallback;
}

function countPhrase(
  t: TFunction,
  value: number,
  oneKey: TranslationKey,
  otherKey: TranslationKey,
): string {
  return t(value === 1 ? oneKey : otherKey, { count: countText(value) });
}

function scoreText(value: number): string {
  return formatNumber(value, { maximumFractionDigits: 15 });
}

function countText(value: number): string {
  return formatNumber(value);
}

function coworkReportHasSignal(row: CoworkReadinessRow): boolean {
  return (row.coworkReportTotalTasks ?? 0) > 0 || (row.coworkReportActiveDays ?? 0) > 0;
}

function coworkReportEvidencePhrase(t: TFunction, row: CoworkReadinessRow): string {
  const days = row.coworkReportActiveDays ?? 0;
  if (row.coworkReportTotalTasks !== null) {
    return t('copilotAdoptionCowork.server.rationale.reportEvidence.tasks', {
      tasks: countText(row.coworkReportTotalTasks),
      taskWord: row.coworkReportTotalTasks === 1
        ? t('copilotAdoptionCowork.server.rationale.task')
        : t('copilotAdoptionCowork.server.rationale.tasks'),
      days: countText(days),
      dayWord: days === 1
        ? t('copilotAdoptionCowork.server.rationale.day')
        : t('copilotAdoptionCowork.server.rationale.days'),
    });
  }

  return t('copilotAdoptionCowork.server.rationale.reportEvidence.activeDays', {
    days: countText(days),
    dayWord: days === 1
      ? t('copilotAdoptionCowork.server.rationale.day')
      : t('copilotAdoptionCowork.server.rationale.days'),
  });
}

function coworkWorkloadPhrase(t: TFunction, row: CoworkReadinessRow): string {
  const workload: string[] = [];
  if (row.teamsMeetings > 0) {
    workload.push(countPhrase(
      t,
      row.teamsMeetings,
      'copilotAdoptionCowork.server.rationale.workload.meeting.one',
      'copilotAdoptionCowork.server.rationale.workload.meeting.other',
    ));
  }
  if (row.teamsMessages > 0) {
    workload.push(countPhrase(
      t,
      row.teamsMessages,
      'copilotAdoptionCowork.server.rationale.workload.teamsMessage.one',
      'copilotAdoptionCowork.server.rationale.workload.teamsMessage.other',
    ));
  }
  const mail = row.emailsSent + row.emailsRead;
  if (mail > 0) {
    workload.push(countPhrase(
      t,
      mail,
      'copilotAdoptionCowork.server.rationale.workload.email.one',
      'copilotAdoptionCowork.server.rationale.workload.email.other',
    ));
  }
  if (row.filesViewedOrEdited > 0) {
    workload.push(countPhrase(
      t,
      row.filesViewedOrEdited,
      'copilotAdoptionCowork.server.rationale.workload.file.one',
      'copilotAdoptionCowork.server.rationale.workload.file.other',
    ));
  }

  return workload.length > 0
    ? workload.join(', ')
    : t('copilotAdoptionCowork.server.rationale.workload.none');
}

export function coworkRationaleText(t: TFunction, row: CoworkReadinessRow, options: CopilotAdoptionOptions): string {
  if (activeLocale().startsWith('en')) return row.rationale;

  const regularDays = Math.max(1, options.coworkRegularMinActiveDays);
  switch (row.tier) {
    case 'established':
      if (coworkReportHasSignal(row)) {
        return t('copilotAdoptionCowork.server.rationale.established.report', {
          evidence: coworkReportEvidencePhrase(t, row),
        });
      }
      return t('copilotAdoptionCowork.server.rationale.established.audit', {
        interactions: countText(row.coworkInteractions),
        interactionWord: row.coworkInteractions === 1
          ? t('copilotAdoptionCowork.server.rationale.interaction')
          : t('copilotAdoptionCowork.server.rationale.interactions'),
        days: countText(row.coworkActiveDays),
        dayWord: row.coworkActiveDays === 1
          ? t('copilotAdoptionCowork.server.rationale.day')
          : t('copilotAdoptionCowork.server.rationale.days'),
      });

    case 'trialling':
      if (coworkReportHasSignal(row)) {
        return t('copilotAdoptionCowork.server.rationale.trialling.report', {
          evidence: coworkReportEvidencePhrase(t, row),
          days: countText(regularDays),
        });
      }
      return t('copilotAdoptionCowork.server.rationale.trialling.audit', {
        interactions: countText(row.coworkInteractions),
        interactionWord: row.coworkInteractions === 1
          ? t('copilotAdoptionCowork.server.rationale.interaction')
          : t('copilotAdoptionCowork.server.rationale.interactions'),
        days: countText(row.coworkActiveDays),
        dayWord: row.coworkActiveDays === 1
          ? t('copilotAdoptionCowork.server.rationale.day')
          : t('copilotAdoptionCowork.server.rationale.days'),
        regularDays: countText(regularDays),
      });

    case 'primeCandidate':
      return t('copilotAdoptionCowork.server.rationale.primeCandidate', {
        fluency: scoreText(row.fluencyScore),
        load: scoreText(row.coordinationLoadScore),
        workload: coworkWorkloadPhrase(t, row),
      });

    case 'buildFluencyFirst':
      return t('copilotAdoptionCowork.server.rationale.buildFluencyFirst', {
        workload: coworkWorkloadPhrase(t, row),
        fluency: scoreText(row.fluencyScore),
        bar: scoreText(options.coworkFluencyMinScore),
      });

    case 'lowCoordinationLoad':
      return t('copilotAdoptionCowork.server.rationale.lowCoordinationLoad', {
        fluency: scoreText(row.fluencyScore),
        load: scoreText(row.coordinationLoadScore),
        workload: coworkWorkloadPhrase(t, row),
      });

    case 'notIndicated':
      return t('copilotAdoptionCowork.server.rationale.notIndicated', {
        fluency: scoreText(row.fluencyScore),
        load: scoreText(row.coordinationLoadScore),
        workload: coworkWorkloadPhrase(t, row),
      });

    default:
      return row.rationale;
  }
}

export function availabilityMessages(t: TFunction, availability: CopilotAdoptionAvailability): string[] {
  const messages: string[] = [];
  if (!availability.userMetadataImportEnabled) {
    messages.push(t('copilotAdoption.server.availability.userMetadataDisabled'));
  }
  if (!availability.copilotAuditImportEnabled && !availability.copilotUsageReportImportEnabled) {
    messages.push(t('copilotAdoption.server.availability.noCopilotSources'));
  }
  if (!availability.copilotAuditImportEnabled && availability.copilotUsageReportImportEnabled) {
    messages.push(t('copilotAdoption.server.availability.auditDisabled'));
  }
  if (!availability.m365UsageReportImportEnabled) {
    messages.push(t('copilotAdoption.server.availability.m365UsageDisabled'));
  }
  return messages.length > 0 ? messages : availability.messages;
}

import type {
  AgentUsageRow,
  CopilotAdoptionAvailability,
  CopilotAdoptionOptions,
  LicenceOpportunityRow,
  LicensedUserAdoptionRow,
} from '../../types/copilotAdoption';
import { AdoptionBand, AgentHealth } from '../../types/copilotAdoption';
import { formatCount } from '../shared/KpiGrid';
import { activeLocale, type TFunction, type TranslationKey, type TranslationValues } from '../../i18n';

function catalogText(t: TFunction, key: TranslationKey, fallback: string, values?: TranslationValues): string {
  if (activeLocale().startsWith('en')) return fallback;
  const translated = t(key, values);
  return translated === key ? fallback : translated;
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

export function reclaimEligibilityReason(t: TFunction, row: LicensedUserAdoptionRow, options: CopilotAdoptionOptions): string {
  if (row.reclaimEligibility === 'excluded' && row.reclaimExclusionReason) {
    return t('copilotAdoptionUsers.server.reclaimReason.excluded', { reason: row.reclaimExclusionReason });
  }
  if (row.accountEnabled === false) {
    return t('copilotAdoptionUsers.server.reclaimReason.disabled');
  }
  if (row.band === AdoptionBand.NeverUsed) {
    if (row.tooNewToJudge) {
      return t('copilotAdoptionUsers.server.reclaimReason.tooNew', {
        tenureBasis: row.tenureBasis ?? '',
        days: options.reclaimGraceDays,
      });
    }
    if (row.accountEnabled === true && row.daysSinceTenureStart !== null) {
      return t('copilotAdoptionUsers.server.reclaimReason.probable', {
        tenureBasis: row.tenureBasis ?? '',
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
          reason: row.reclaimExclusionReason ?? '',
          date: row.reclaimExclusionReviewAfterUtc.slice(0, 10),
        })
      : t('copilotAdoptionUsers.server.recommendedAction.excluded.permanent', { reason: row.reclaimExclusionReason ?? '' });
  }
  if (row.accountEnabled === false) {
    return t('copilotAdoptionUsers.server.recommendedAction.disabled');
  }
  if (row.tooNewToJudge && row.band === AdoptionBand.NeverUsed) {
    return t('copilotAdoptionUsers.server.recommendedAction.tooNew', {
      tenureBasis: row.tenureBasis ?? '',
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

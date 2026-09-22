import { makeStyles, tokens, Text, Card, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { KpiGrid } from '../shared/KpiGrid';
import type { KpiDefinition } from '../shared/KpiGrid';
import GaugeRing from '../charts/GaugeRing';
import TimeSeriesChart from '../charts/TimeSeriesChart';
import DonutChart from '../charts/DonutChart';
import { seriesColor } from '../charts/chartCommon';
import type { TeamsJudgement, TeamsOverview, TeamsOverviewKpis } from '../../types/teamsExplorer';
import { formatNumber, useT, type TFunction, type TranslationKey } from '../../i18n';
import {
  SectionCard,
  WindowNote,
  formatCount,
  formatDecimal,
  formatHours,
  formatPct,
  queryFor,
  reachTone,
  segmentDescription,
  segmentLabel,
  useTeamsStyles,
} from './teamsShared';

const useStyles = makeStyles({
  judgements: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(300px, 1fr))',
    gap: '12px',
    marginTop: '16px',
  },
  judgement: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    padding: '14px 16px',
    borderLeftWidth: '4px',
    borderLeftStyle: 'solid',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  gaugeRow: {
    display: 'flex',
    justifyContent: 'center',
    paddingTop: '8px',
  },
});


function judgementText(
  t: TFunction,
  key: string,
  field: 'headline' | 'detail',
  fallback: string,
  values?: Record<string, string>,
  variant?: string,
): string {
  if (!key) return fallback;
  const catalogKey = `teamsExplorer.judgement.${key}.${field}${variant ? `.${variant}` : ''}` as TranslationKey;
  const translated = t(catalogKey, values);
  return translated === catalogKey ? fallback : translated;
}

function adoptionBandLabel(percent: number, t: TFunction): string {
  if (percent < 40) return t('teamsExplorer.judgement.reach.band.needsAttention');
  return percent < 70
    ? t('teamsExplorer.judgement.reach.band.progressing')
    : t('teamsExplorer.judgement.reach.band.healthy');
}

function firstPercent(text: string): string | undefined {
  const match = text.match(/([0-9][0-9.,]*)%/);
  if (!match) return undefined;
  const value = Number(match[1].replace(/,/g, ''));
  return Number.isFinite(value) ? formatPct(value) : match[0];
}

function serverCount(text: string | undefined): string | undefined {
  if (!text) return undefined;
  const value = Number(text.replace(/,/g, ''));
  return Number.isFinite(value) ? formatCount(value) : text;
}

function formatOneDecimal(value: number): string {
  return formatNumber(Math.round(value * 10) / 10, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
}

function translatedJudgement(judgement: TeamsJudgement, kpis: TeamsOverviewKpis, t: TFunction) {
  switch (judgement.key) {
    case 'reach': {
      const values = {
        reach: formatPct(kpis.reachPct),
        active: formatCount(kpis.activeUsers),
        known: formatCount(kpis.knownUsers),
        band: adoptionBandLabel(kpis.reachPct, t).toLowerCase(),
      };
      return {
        headline: judgementText(t, judgement.key, 'headline', judgement.headline, values),
        detail: judgementText(t, judgement.key, 'detail', judgement.detail, values),
      };
    }
    case 'open-collaboration': {
      const values = { share: formatPct(kpis.openCollaborationPct) };
      return {
        headline: judgementText(t, judgement.key, 'headline', judgement.headline, values),
        detail: judgementText(
          t,
          judgement.key,
          'detail',
          judgement.detail,
          values,
          judgement.tone === 'warning' ? 'warning' : 'neutral',
        ),
      };
    }
    case 'after-hours': {
      const values = { share: firstPercent(judgement.headline) ?? '' };
      return {
        headline: judgementText(t, judgement.key, 'headline', judgement.headline, values),
        detail: judgementText(t, judgement.key, 'detail', judgement.detail, values),
      };
    }
    case 'organiser-concentration': {
      const values = { share: firstPercent(judgement.headline) ?? '' };
      return {
        headline: judgementText(t, judgement.key, 'headline', judgement.headline, values),
        detail: judgementText(t, judgement.key, 'detail', judgement.detail, values),
      };
    }
    case 'meeting-load': {
      const values = { meetingsPerActiveUser: formatOneDecimal(kpis.meetingsPerActiveUser) };
      return {
        headline: judgementText(t, judgement.key, 'headline', judgement.headline, values),
        detail: judgementText(t, judgement.key, 'detail', judgement.detail, values),
      };
    }
    case 'ownerless-teams': {
      const counts = judgement.headline.match(/([0-9][0-9,]*) of ([0-9][0-9,]*)/);
      const values = {
        ownerless: serverCount(counts?.[1]) ?? '',
        total: serverCount(counts?.[2]) ?? formatCount(kpis.totalTeams),
      };
      return {
        headline: judgementText(t, judgement.key, 'headline', judgement.headline, values),
        detail: judgementText(t, judgement.key, 'detail', judgement.detail, values),
      };
    }
    case 'team-sprawl': {
      const values = {
        active: formatCount(kpis.activeTeams),
        total: formatCount(kpis.totalTeams),
        share: formatPct(kpis.totalTeams > 0 ? (kpis.activeTeams / kpis.totalTeams) * 100 : 0),
        dormant: formatCount(kpis.totalTeams - kpis.activeTeams),
      };
      return {
        headline: judgementText(t, judgement.key, 'headline', judgement.headline, values),
        detail: judgementText(
          t,
          judgement.key,
          'detail',
          judgement.detail,
          values,
          judgement.tone === 'warning' ? 'warning' : 'good',
        ),
      };
    }
    default:
      return {
        headline: judgementText(t, judgement.key, 'headline', judgement.headline),
        detail: judgementText(t, judgement.key, 'detail', judgement.detail),
      };
  }
}

const TONE_COLOUR: Record<string, string> = {
  good: tokens.colorPaletteGreenBorderActive,
  warning: tokens.colorPaletteYellowBorderActive,
  critical: tokens.colorPaletteRedBorderActive,
  neutral: tokens.colorNeutralStroke1,
};

/**
 * The landing tab: is Teams being used, and is anything obviously wrong?
 *
 * Deliberately opens with written findings rather than charts. An executive reading this does not
 * want to derive "channel conversation is unusually low" from a donut - they want to be told, and
 * then shown the figure it came from. The charts below are the evidence for the sentences above.
 */
export default function OverviewPanel({ data }: { data: TeamsOverview }) {
  const styles = useStyles();
  const shared = useTeamsStyles();
  const t = useT();
  const { kpis } = data;

  const kpiItems: KpiDefinition[] = [
    {
      key: 'reach',
      label: t('teamsExplorer.overview.kpi.teamsReach.label'),
      value: formatPct(kpis.reachPct),
      hint: t('teamsExplorer.overview.kpi.teamsReach.hint', {
        active: formatCount(kpis.activeUsers),
        known: formatCount(kpis.knownUsers),
      }),
      tone: reachTone(kpis.reachPct),
      info: {
        what: t('teamsExplorer.overview.kpi.teamsReach.what'),
        how:
          t('teamsExplorer.overview.kpi.teamsReach.how'),
        formula: t('teamsExplorer.overview.kpi.teamsReach.formula'),
        source:
          t('teamsExplorer.overview.kpi.teamsReach.source'),
      },
    },
    {
      key: 'channel-share',
      label: t('teamsExplorer.overview.kpi.openCollaboration.label'),
      value: formatPct(kpis.openCollaborationPct),
      hint: t('teamsExplorer.overview.kpi.openCollaboration.hint'),
      tone: kpis.openCollaborationPct < 15 ? 'warning' : 'neutral',
      info: {
        what: t('teamsExplorer.overview.kpi.openCollaboration.what'),
        how:
          t('teamsExplorer.overview.kpi.openCollaboration.how'),
        formula: t('teamsExplorer.overview.kpi.openCollaboration.formula'),
        source: t('teamsExplorer.overview.kpi.openCollaboration.source'),
      },
    },
    {
      key: 'meetings',
      label: t('teamsExplorer.overview.kpi.meetingsPerActiveUser.label'),
      value: formatDecimal(kpis.meetingsPerActiveUser),
      hint: t('teamsExplorer.overview.kpi.meetingsPerActiveUser.hint', {
        meetings: formatCount(kpis.meetingsAttended),
      }),
      info: {
        what: t('teamsExplorer.overview.kpi.meetingsPerActiveUser.what'),
        how:
          t('teamsExplorer.overview.kpi.meetingsPerActiveUser.how'),
        formula: t('teamsExplorer.overview.kpi.meetingsPerActiveUser.formula'),
        source: t('teamsExplorer.source.usageReports'),
      },
    },
    {
      key: 'audio-hours',
      label: t('teamsExplorer.overview.kpi.audioHours.label'),
      value: formatHours(kpis.audioHours),
      hint: t('teamsExplorer.overview.kpi.audioHours.hint', {
        video: formatPct(kpis.videoSharePct),
        sharing: formatPct(kpis.screenShareSharePct),
      }),
      info: {
        what: t('teamsExplorer.overview.kpi.audioHours.what'),
        how:
          t('teamsExplorer.overview.kpi.audioHours.how'),
        formula: t('teamsExplorer.overview.kpi.audioHours.formula'),
        source: t('teamsExplorer.source.usageReports'),
      },
    },
    {
      key: 'calls',
      label: t('teamsExplorer.overview.kpi.callsRecorded.label'),
      value: formatCount(kpis.calls),
      hint: t('teamsExplorer.overview.kpi.callsRecorded.hint'),
      info: {
        what: t('teamsExplorer.overview.kpi.callsRecorded.what'),
        how:
          t('teamsExplorer.overview.kpi.callsRecorded.how'),
        source: t('teamsExplorer.source.graphCallRecords'),
      },
    },
    {
      key: 'teams',
      label: t('teamsExplorer.overview.kpi.activeTeams.label'),
      value: `${formatCount(kpis.activeTeams)} / ${formatCount(kpis.totalTeams)}`,
      hint: t('teamsExplorer.overview.kpi.activeTeams.hint', {
        activeChannels: formatCount(kpis.activeChannels),
        totalChannels: formatCount(kpis.totalChannels),
      }),
      info: {
        what: t('teamsExplorer.overview.kpi.activeTeams.what'),
        how:
          t('teamsExplorer.overview.kpi.activeTeams.how'),
        source: t('teamsExplorer.overview.kpi.activeTeams.source'),
      },
    },
  ];

  const trendQuery = queryFor(data.queries, 'overview-trend');
  const segmentQuery = queryFor(data.queries, 'overview-segments');

  const activitySeries = [
    {
      name: t('teamsExplorer.overview.series.activeUsers'),
      points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.activeUsers })),
    },
  ];

  const messageSeries = [
    {
      name: t('teamsExplorer.overview.series.channelMessages'),
      points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.channelMessages })),
    },
    {
      name: t('teamsExplorer.overview.series.privateChatMessages'),
      points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.privateMessages })),
    },
    {
      name: t('teamsExplorer.overview.series.meetingsAttended'),
      points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.meetingsAttended })),
    },
  ];

  const segmentCategories = data.segmentMix.map((slice) => ({
    label: segmentLabel(t, slice.segment, slice.label),
    value: slice.users,
  }));
  const measuredUsers = data.segmentMix.reduce((sum, slice) => sum + slice.users, 0);

  return (
    <div>
      <WindowNote window={data.window} />

      <div style={{ marginTop: '16px' }}>
        <KpiGrid items={kpiItems} />
      </div>

      {data.judgements.length > 0 && (
        <div className={styles.judgements}>
          {data.judgements.map((judgement) => {
            const text = translatedJudgement(judgement, kpis, t);
            return (
            <Card
              key={judgement.key}
              className={styles.judgement}
              style={{ borderLeftColor: TONE_COLOUR[judgement.tone] ?? TONE_COLOUR.neutral }}
            >
              <Text weight="semibold">{text.headline}</Text>
              <Text size={200} className={styles.muted}>
                {text.detail}
              </Text>
            </Card>
            );
          })}
        </div>
      )}

      <div className={shared.grid}>
        <SectionCard
          title={t('teamsExplorer.overview.reachAgainstDirectory.title')}
          description={t('teamsExplorer.overview.reachAgainstDirectory.description')}
          query={queryFor(data.queries, 'overview-usage')}
          isEmpty={kpis.knownUsers === 0}
          emptyMessage={t('teamsExplorer.overview.reachAgainstDirectory.empty')}
        >
          <div className={styles.gaugeRow}>
            <GaugeRing
              value={kpis.reachPct}
              label={formatPct(kpis.reachPct)}
              sublabel={t('teamsExplorer.overview.reachAgainstDirectory.sublabel', {
                active: formatCount(kpis.activeUsers),
                known: formatCount(kpis.knownUsers),
              })}
            />
          </div>
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.overview.engagementMix.title')}
          description={t('teamsExplorer.overview.engagementMix.description')}
          query={segmentQuery}
          isEmpty={measuredUsers === 0}
          emptyMessage={t('teamsExplorer.overview.engagementMix.empty')}
        >
          <DonutChart
            categories={segmentCategories}
            colours={segmentCategories.map((_, i) => seriesColor(i))}
            centreValue={formatCount(measuredUsers)}
            centreLabel={t('teamsExplorer.overview.engagementMix.centreLabel')}
          />
          <Text size={200} className={styles.muted}>
            {data.segmentMix
              .map((slice) => `${segmentLabel(t, slice.segment, slice.label)}: ${segmentDescription(t, slice.segment, slice.description)}`)
              .join(' ')}
          </Text>
        </SectionCard>
      </div>

      <div className={shared.stack}>
        <SectionCard
          title={t('teamsExplorer.overview.weeklyActiveUsers.title')}
          description={t('teamsExplorer.overview.weeklyActiveUsers.description')}
          query={trendQuery}
          isEmpty={data.trend.length === 0}
        >
          <TimeSeriesChart series={activitySeries} valueLabel={t('teamsExplorer.overview.valueLabel.users')} height={240} />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.overview.whatPeopleAreDoing.title')}
          description={t('teamsExplorer.overview.whatPeopleAreDoing.description')}
          query={trendQuery}
          isEmpty={data.trend.length === 0}
        >
          <TimeSeriesChart series={messageSeries} valueLabel={t('teamsExplorer.overview.valueLabel.count')} height={260} />
        </SectionCard>
      </div>

      {kpis.knownUsers > 0 && kpis.activeUsers === 0 && (
        <MessageBar intent="warning" style={{ marginTop: '16px' }}>
          <MessageBarBody>
            {t('teamsExplorer.overview.noActiveUsersWarning')}
          </MessageBarBody>
        </MessageBar>
      )}
    </div>
  );
}

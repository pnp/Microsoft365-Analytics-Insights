import { makeStyles, tokens, Text, Select } from '@fluentui/react-components';
import { KpiGrid } from '../shared/KpiGrid';
import type { KpiDefinition } from '../shared/KpiGrid';
import CategoryBarChart from '../charts/CategoryBarChart';
import StackedAreaChart from '../charts/StackedAreaChart';
import TreemapChart from '../charts/TreemapChart';
import type { TeamsAdoption, TeamsGrouping } from '../../types/teamsExplorer';
import { plural, useT, type TranslationKey } from '../../i18n';
import {
  SectionCard,
  WindowNote,
  PrivacyReaderNote,
  figureOrWithheld,
  formatCount,
  formatDecimal,
  formatPct,
  platformLabel,
  queryFor,
  reachTone,
  useTeamsStyles,
  withheldReason,
} from './teamsShared';

/** The demographic dimensions the breakdown can group by, matching `TeamsExplorerQuery.Groupings`. */
export const GROUPINGS: { value: TeamsGrouping; labelKey: TranslationKey }[] = [
  { value: 'department', labelKey: 'teamsExplorer.adoption.group.department' },
  { value: 'country', labelKey: 'teamsExplorer.adoption.group.country' },
  { value: 'office', labelKey: 'teamsExplorer.adoption.group.office' },
  { value: 'jobTitle', labelKey: 'teamsExplorer.adoption.group.jobTitle' },
  { value: 'company', labelKey: 'teamsExplorer.adoption.group.company' },
];

const useStyles = makeStyles({
  controls: {
    display: 'flex',
    alignItems: 'flex-end',
    gap: '12px',
    marginTop: '12px',
    flexWrap: 'wrap',
  },
  field: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  fieldLabel: {
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
    color: tokens.colorNeutralForeground3,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
});

/**
 * Adoption and reach: how many people use Teams, how habitually, on what, and who is drifting away.
 */
export default function AdoptionPanel({
  data,
  groupBy,
  onGroupByChange,
  demographicsAvailable,
}: {
  data: TeamsAdoption;
  groupBy: TeamsGrouping;
  onGroupByChange: (value: TeamsGrouping) => void;
  /** False when the user metadata import is off, so every group would read "(not set)". */
  demographicsAvailable: boolean;
}) {
  const styles = useStyles();
  const shared = useTeamsStyles();
  const t = useT();
  const { rhythm, lifecycle } = data;

  // For a reader without See PII, a figure about one to four people comes back null: a dash that says why, never a zero,
  // and no tone worked out from it.
  const withheld = withheldReason(t, data.minimumGroupSize);

  const kpis: KpiDefinition[] = [
    {
      key: 'dau',
      label: t('teamsExplorer.adoption.kpi.averageDailyUsers.label'),
      value: figureOrWithheld(rhythm.meanDailyActiveUsers, formatDecimal, withheld),
      hint: t('teamsExplorer.adoption.kpi.averageDailyUsers.hint', { days: formatCount(data.window.workingDays) }),
      info: {
        what: t('teamsExplorer.adoption.kpi.averageDailyUsers.what'),
        how:
          t('teamsExplorer.adoption.kpi.averageDailyUsers.how'),
        formula: t('teamsExplorer.adoption.kpi.averageDailyUsers.formula'),
        source: t('teamsExplorer.adoption.source.usageReports'),
      },
    },
    {
      key: 'wau',
      label: t('teamsExplorer.adoption.kpi.weeklyActiveUsers.label'),
      value: figureOrWithheld(rhythm.weeklyActiveUsers, formatCount, withheld),
      hint: t('teamsExplorer.adoption.kpi.weeklyActiveUsers.hint'),
      info: {
        what: t('teamsExplorer.adoption.kpi.weeklyActiveUsers.what'),
        formula: t('teamsExplorer.adoption.kpi.weeklyActiveUsers.formula'),
        source: t('teamsExplorer.adoption.source.usageReports'),
      },
    },
    {
      key: 'mau',
      label: t('teamsExplorer.adoption.kpi.monthlyActiveUsers.label'),
      value: figureOrWithheld(rhythm.monthlyActiveUsers, formatCount, withheld),
      hint: t('teamsExplorer.adoption.kpi.monthlyActiveUsers.hint'),
      info: {
        what: t('teamsExplorer.adoption.kpi.monthlyActiveUsers.what'),
        how:
          t('teamsExplorer.adoption.kpi.monthlyActiveUsers.how'),
        formula: t('teamsExplorer.adoption.kpi.monthlyActiveUsers.formula'),
        source: t('teamsExplorer.adoption.source.usageReports'),
      },
    },
    {
      key: 'stickiness',
      label: t('teamsExplorer.adoption.kpi.stickiness.label'),
      value: figureOrWithheld(rhythm.stickinessPct, formatPct, withheld),
      hint: t('teamsExplorer.adoption.kpi.stickiness.hint'),
      tone: rhythm.stickinessPct == null ? 'neutral' : reachTone(rhythm.stickinessPct),
      info: {
        what: t('teamsExplorer.adoption.kpi.stickiness.what'),
        how:
          t('teamsExplorer.adoption.kpi.stickiness.how'),
        formula: t('teamsExplorer.adoption.kpi.stickiness.formula'),
        source: t('teamsExplorer.adoption.source.usageReports'),
      },
    },
    {
      key: 'new',
      label: t('teamsExplorer.adoption.kpi.newlyActive.label'),
      value: figureOrWithheld(lifecycle.newUsers, formatCount, withheld),
      hint: t('teamsExplorer.adoption.kpi.newlyActive.hint'),
      tone: lifecycle.newUsers == null ? 'neutral' : 'good',
      info: {
        what: t('teamsExplorer.adoption.kpi.newlyActive.what'),
        how:
          t('teamsExplorer.adoption.kpi.newlyActive.how'),
        source: t('teamsExplorer.adoption.source.usageReports'),
      },
    },
    {
      key: 'lapsed',
      label: t('teamsExplorer.adoption.kpi.lapsed.label'),
      value: figureOrWithheld(lifecycle.lapsedUsers, formatCount, withheld),
      hint: t('teamsExplorer.adoption.kpi.lapsed.hint'),
      tone:
        lifecycle.lapsedUsers == null || lifecycle.newUsers == null
          ? 'neutral'
          : lifecycle.lapsedUsers > lifecycle.newUsers
            ? 'critical'
            : 'warning',
      info: {
        what: t('teamsExplorer.adoption.kpi.lapsed.what'),
        how:
          t('teamsExplorer.adoption.kpi.lapsed.how'),
        source: t('teamsExplorer.adoption.source.usageReports'),
      },
    },
  ];

  const segmentSeries = [
    {
      name: t('teamsExplorer.adoption.segment.power'),
      points: data.segmentTrend.map((p) => ({ weekStart: p.weekStart, value: p.power })),
    },
    {
      name: t('teamsExplorer.adoption.segment.regular'),
      points: data.segmentTrend.map((p) => ({ weekStart: p.weekStart, value: p.regular })),
    },
    {
      name: t('teamsExplorer.adoption.segment.light'),
      points: data.segmentTrend.map((p) => ({ weekStart: p.weekStart, value: p.light })),
    },
  ];

  const otherGroups = data.otherGroups ?? 0;
  const minimumGroupSize = data.minimumGroupSize ?? null;
  // The combined row a reader without See PII gets in place of every group too small to name. It is left out when even
  // all those groups together are too few people, so the note says which of the two happened.
  const hasOtherRow = data.breakdown.some((row) => row.other);
  const rowLabel = (row: TeamsAdoption['breakdown'][number]): string =>
    row.other
      ? t(plural(otherGroups, 'teamsExplorer.adoption.otherGroups.one', 'teamsExplorer.adoption.otherGroups.other'), {
          count: formatCount(otherGroups),
        })
      : row.name ?? '';
  const otherNote =
    otherGroups > 0 && minimumGroupSize != null && data.breakdown.length > 0
      ? t(hasOtherRow ? 'teamsExplorer.adoption.reachBy.otherNote' : 'teamsExplorer.adoption.reachBy.leftOutNote', {
          count: formatCount(minimumGroupSize),
        })
      : undefined;
  const allGroupsSmall =
    otherGroups > 0 && minimumGroupSize != null && data.breakdown.length === 0
      ? t('teamsExplorer.adoption.reachBy.allGroupsSmall', { count: formatCount(minimumGroupSize) })
      : undefined;

  const reachCategories = data.breakdown.map((row) => ({
    label: rowLabel(row),
    value: Math.round(row.reachPct * 10) / 10,
  }));

  const volumeCategories = data.breakdown.map((row) => ({
    label: rowLabel(row),
    value: row.activeUsers,
  }));

  const deviceCategories = data.devices.flatMap((row) => (row.users == null ? [] : [{ label: platformLabel(t, row), value: row.users }]));
  const minimumText = formatCount(minimumGroupSize ?? 5);
  const devicesWithheldNote = data.devices.some((row) => row.withheld)
    ? t('teamsExplorer.adoption.devices.withheldNote', { count: minimumText })
    : undefined;
  const weeksWithheldNote = data.segmentTrend.some((p) => p.withheld)
    ? t('teamsExplorer.adoption.engagementOverTime.withheldNote', { count: minimumText })
    : undefined;

  const groupLabel = t(GROUPINGS.find((g) => g.value === groupBy)?.labelKey ?? 'teamsExplorer.adoption.group.department');

  return (
    <div>
      <WindowNote window={data.window} includeUsage={false} />
      <PrivacyReaderNote minimumGroupSize={minimumGroupSize} />

      <div style={{ marginTop: '16px' }}>
        <KpiGrid items={kpis} />
      </div>

      <div className={shared.stack}>
        <SectionCard
          title={t('teamsExplorer.adoption.engagementOverTime.title')}
          description={t('teamsExplorer.adoption.engagementOverTime.description')}
          query={queryFor(data.queries, 'adoption-segment-trend')}
          isEmpty={data.segmentTrend.length === 0}
          note={[t('teamsExplorer.adoption.engagementOverTime.note'), weeksWithheldNote].filter(Boolean).join(' ')}
        >
          <StackedAreaChart
            series={segmentSeries}
            valueLabel={t('teamsExplorer.adoption.valueLabel.users')}
            withheldLabel={weeksWithheldNote ? withheld : undefined}
          />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.adoption.reachBy.title', { group: groupLabel.toLowerCase() })}
          description={t('teamsExplorer.adoption.reachBy.description')}
          query={queryFor(data.queries, 'adoption-breakdown')}
          isEmpty={data.breakdown.length === 0}
          emptyMessage={
            allGroupsSmall ??
            (demographicsAvailable
              ? t('teamsExplorer.adoption.reachBy.emptyNoGroups')
              : t('teamsExplorer.adoption.reachBy.emptyMetadataUnavailable'))
          }
          note={
            demographicsAvailable
              ? otherNote
              : t('teamsExplorer.adoption.reachBy.metadataUnavailableNote')
          }
        >
          <div className={styles.controls}>
            <div className={styles.field}>
              <Text size={200} className={styles.fieldLabel}>
                {t('teamsExplorer.adoption.groupBy')}
              </Text>
              <Select
                value={groupBy}
                onChange={(_: any, d: any) => onGroupByChange(d.value as TeamsGrouping)}
                aria-label={t('teamsExplorer.adoption.groupByAria')}
              >
                {GROUPINGS.map((option) => (
                  <option key={option.value} value={option.value}>
                    {t(option.labelKey)}
                  </option>
                ))}
              </Select>
            </div>
          </div>

          <div style={{ marginTop: '12px' }}>
            <CategoryBarChart categories={reachCategories} valueLabel={t('teamsExplorer.adoption.valueLabel.reachPct')} />
          </div>
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.adoption.activeUsersBy.title', { group: groupLabel.toLowerCase() })}
          description={t('teamsExplorer.adoption.activeUsersBy.description')}
          query={queryFor(data.queries, 'adoption-breakdown')}
          isEmpty={volumeCategories.length === 0}
        >
          <TreemapChart categories={volumeCategories} valueLabel={t('teamsExplorer.adoption.valueLabel.activeUsers')} />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.adoption.devices.title')}
          description={t('teamsExplorer.adoption.devices.description')}
          query={queryFor(data.queries, 'adoption-devices')}
          isEmpty={deviceCategories.length === 0}
          emptyMessage={devicesWithheldNote ?? t('teamsExplorer.adoption.devices.empty')}
          note={[t('teamsExplorer.adoption.devices.note'), deviceCategories.length > 0 ? devicesWithheldNote : undefined].filter(Boolean).join(' ')}
        >
          <CategoryBarChart categories={deviceCategories} valueLabel={t('teamsExplorer.adoption.valueLabel.usersLower')} />
        </SectionCard>
      </div>
    </div>
  );
}

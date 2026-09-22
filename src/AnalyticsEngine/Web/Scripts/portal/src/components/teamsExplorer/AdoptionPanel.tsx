import { makeStyles, tokens, Text, Select } from '@fluentui/react-components';
import { KpiGrid } from '../shared/KpiGrid';
import type { KpiDefinition } from '../shared/KpiGrid';
import CategoryBarChart from '../charts/CategoryBarChart';
import StackedAreaChart from '../charts/StackedAreaChart';
import TreemapChart from '../charts/TreemapChart';
import type { TeamsAdoption, TeamsGrouping } from '../../types/teamsExplorer';
import { useT, type TranslationKey } from '../../i18n';
import {
  SectionCard,
  WindowNote,
  formatCount,
  formatDecimal,
  formatPct,
  queryFor,
  reachTone,
  useTeamsStyles,
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

  const kpis: KpiDefinition[] = [
    {
      key: 'dau',
      label: t('teamsExplorer.adoption.kpi.averageDailyUsers.label'),
      value: formatDecimal(rhythm.meanDailyActiveUsers),
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
      value: formatCount(rhythm.weeklyActiveUsers),
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
      value: formatCount(rhythm.monthlyActiveUsers),
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
      value: formatPct(rhythm.stickinessPct),
      hint: t('teamsExplorer.adoption.kpi.stickiness.hint'),
      tone: reachTone(rhythm.stickinessPct),
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
      value: formatCount(lifecycle.newUsers),
      hint: t('teamsExplorer.adoption.kpi.newlyActive.hint'),
      tone: 'good',
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
      value: formatCount(lifecycle.lapsedUsers),
      hint: t('teamsExplorer.adoption.kpi.lapsed.hint'),
      tone: lifecycle.lapsedUsers > lifecycle.newUsers ? 'critical' : 'warning',
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

  const reachCategories = data.breakdown.map((row) => ({
    label: row.name,
    value: Math.round(row.reachPct * 10) / 10,
  }));

  const volumeCategories = data.breakdown.map((row) => ({
    label: row.name,
    value: row.activeUsers,
  }));

  const deviceCategories = data.devices.map((row) => ({
    label: row.platform,
    value: row.users,
  }));

  const groupLabel = t(GROUPINGS.find((g) => g.value === groupBy)?.labelKey ?? 'teamsExplorer.adoption.group.department');

  return (
    <div>
      <WindowNote window={data.window} includeUsage={false} />

      <div style={{ marginTop: '16px' }}>
        <KpiGrid items={kpis} />
      </div>

      <div className={shared.stack}>
        <SectionCard
          title={t('teamsExplorer.adoption.engagementOverTime.title')}
          description={t('teamsExplorer.adoption.engagementOverTime.description')}
          query={queryFor(data.queries, 'adoption-segment-trend')}
          isEmpty={data.segmentTrend.length === 0}
          note={
            t('teamsExplorer.adoption.engagementOverTime.note')
          }
        >
          <StackedAreaChart series={segmentSeries} valueLabel={t('teamsExplorer.adoption.valueLabel.users')} />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.adoption.reachBy.title', { group: groupLabel.toLowerCase() })}
          description={t('teamsExplorer.adoption.reachBy.description')}
          query={queryFor(data.queries, 'adoption-breakdown')}
          isEmpty={data.breakdown.length === 0}
          emptyMessage={
            demographicsAvailable
              ? t('teamsExplorer.adoption.reachBy.emptyNoGroups')
              : t('teamsExplorer.adoption.reachBy.emptyMetadataUnavailable')
          }
          note={
            demographicsAvailable
              ? undefined
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
          emptyMessage={t('teamsExplorer.adoption.devices.empty')}
          note={
            t('teamsExplorer.adoption.devices.note')
          }
        >
          <CategoryBarChart categories={deviceCategories} valueLabel={t('teamsExplorer.adoption.valueLabel.usersLower')} />
        </SectionCard>
      </div>
    </div>
  );
}

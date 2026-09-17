import { makeStyles, tokens, Text, Select } from '@fluentui/react-components';
import { KpiGrid } from '../shared/KpiGrid';
import type { KpiDefinition } from '../shared/KpiGrid';
import CategoryBarChart from '../charts/CategoryBarChart';
import StackedAreaChart from '../charts/StackedAreaChart';
import TreemapChart from '../charts/TreemapChart';
import type { TeamsAdoption, TeamsGrouping } from '../../types/teamsExplorer';
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
export const GROUPINGS: { value: TeamsGrouping; label: string }[] = [
  { value: 'department', label: 'Department' },
  { value: 'country', label: 'Country' },
  { value: 'office', label: 'Office' },
  { value: 'jobTitle', label: 'Job title' },
  { value: 'company', label: 'Company' },
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
  const { rhythm, lifecycle } = data;

  const kpis: KpiDefinition[] = [
    {
      key: 'dau',
      label: 'Average daily users',
      value: formatDecimal(rhythm.meanDailyActiveUsers),
      hint: `Across ${data.window.workingDays} working days`,
      info: {
        what: 'The mean number of people active in Teams on a working day.',
        how:
          'Weekends are excluded from the average. Including them would drag the figure down for '
          + 'every organisation that does not work at the weekend, which is almost all of them.',
        formula: 'mean(daily active users on Mon-Fri)',
        source: 'Microsoft 365 usage reports.',
      },
    },
    {
      key: 'wau',
      label: 'Weekly active users',
      value: formatCount(rhythm.weeklyActiveUsers),
      hint: 'Last 7 days of the reporting window',
      info: {
        what: 'Distinct people active in Teams in the last 7 days covered by the usage reports.',
        formula: 'count(distinct users with any activity in the trailing 7 days)',
        source: 'Microsoft 365 usage reports.',
      },
    },
    {
      key: 'mau',
      label: 'Monthly active users',
      value: formatCount(rhythm.monthlyActiveUsers),
      hint: 'Last 28 days of the reporting window',
      info: {
        what: 'Distinct people active in Teams in the last 28 days covered by the usage reports.',
        how:
          'Capped at the reporting window, so a 7-day window reports the same figure for weekly and '
          + 'monthly rather than quietly reaching outside the period you asked for.',
        formula: 'count(distinct users with any activity in the trailing 28 days)',
        source: 'Microsoft 365 usage reports.',
      },
    },
    {
      key: 'stickiness',
      label: 'Stickiness',
      value: formatPct(rhythm.stickinessPct),
      hint: 'Daily users as a share of monthly users',
      tone: reachTone(rhythm.stickinessPct),
      info: {
        what: 'How much of the monthly audience shows up on any given working day.',
        how:
          '100% would mean everyone who uses Teams at all uses it every working day. A low figure '
          + 'with high monthly reach means Teams is something people visit, not somewhere they work.',
        formula: 'average daily active users / monthly active users x 100',
        source: 'Microsoft 365 usage reports.',
      },
    },
    {
      key: 'new',
      label: 'Newly active',
      value: formatCount(lifecycle.newUsers),
      hint: 'Active in the second half of the period only',
      tone: 'good',
      info: {
        what: 'People who started using Teams during this period.',
        how:
          'The window is split in half; someone active only in the later half is newly active, and '
          + 'someone active only in the earlier half has lapsed.',
        source: 'Microsoft 365 usage reports.',
      },
    },
    {
      key: 'lapsed',
      label: 'Lapsed',
      value: formatCount(lifecycle.lapsedUsers),
      hint: 'Active in the first half only',
      tone: lifecycle.lapsedUsers > lifecycle.newUsers ? 'critical' : 'warning',
      info: {
        what: 'People who were using Teams and then stopped during this period.',
        how:
          'The most actionable number on this tab: these are people who had already adopted Teams, '
          + 'so something changed. More lapsed than newly active means the base is shrinking.',
        source: 'Microsoft 365 usage reports.',
      },
    },
  ];

  const segmentSeries = [
    {
      name: 'Power',
      points: data.segmentTrend.map((p) => ({ weekStart: p.weekStart, value: p.power })),
    },
    {
      name: 'Regular',
      points: data.segmentTrend.map((p) => ({ weekStart: p.weekStart, value: p.regular })),
    },
    {
      name: 'Light',
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

  const groupLabel = GROUPINGS.find((g) => g.value === groupBy)?.label ?? 'Department';

  return (
    <div>
      <WindowNote window={data.window} includeUsage={false} />

      <div style={{ marginTop: '16px' }}>
        <KpiGrid items={kpis} />
      </div>

      <div className={shared.stack}>
        <SectionCard
          title="Engagement over time"
          description="How the split between power, regular and light users moves week to week."
          query={queryFor(data.queries, 'adoption-segment-trend')}
          isEmpty={data.segmentTrend.length === 0}
          note={
            'Each week is judged against its own number of working days, so a short first or last '
            + 'week does not push everyone into the light band.'
          }
        >
          <StackedAreaChart series={segmentSeries} valueLabel="Users" />
        </SectionCard>

        <SectionCard
          title={`Reach by ${groupLabel.toLowerCase()}`}
          description="Where Teams has landed, and where it has not."
          query={queryFor(data.queries, 'adoption-breakdown')}
          isEmpty={data.breakdown.length === 0}
          emptyMessage={
            demographicsAvailable
              ? 'No users could be grouped for this period.'
              : 'User metadata is not being imported, so there is nothing to group by. Switch on the Graph user metadata import.'
          }
          note={
            demographicsAvailable
              ? undefined
              : 'User metadata is not being imported, so every group will read "(not set)".'
          }
        >
          <div className={styles.controls}>
            <div className={styles.field}>
              <Text size={200} className={styles.fieldLabel}>
                Group by
              </Text>
              <Select
                value={groupBy}
                onChange={(_, d) => onGroupByChange(d.value as TeamsGrouping)}
                aria-label="Group the adoption breakdown by"
              >
                {GROUPINGS.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </Select>
            </div>
          </div>

          <div style={{ marginTop: '12px' }}>
            <CategoryBarChart categories={reachCategories} valueLabel="% reach" />
          </div>
        </SectionCard>

        <SectionCard
          title={`Active users by ${groupLabel.toLowerCase()}`}
          description="Where the people actually using Teams are, by volume."
          query={queryFor(data.queries, 'adoption-breakdown')}
          isEmpty={volumeCategories.length === 0}
        >
          <TreemapChart categories={volumeCategories} valueLabel="active users" />
        </SectionCard>

        <SectionCard
          title="Where people use Teams"
          description="Distinct users seen on each client platform."
          query={queryFor(data.queries, 'adoption-devices')}
          isEmpty={deviceCategories.length === 0}
          emptyMessage="No device usage was reported for this period."
          note={
            'Shares add up to more than 100% on purpose: most people use more than one platform. '
            + 'Mobile reach is the one to watch for frontline and deskless staff.'
          }
        >
          <CategoryBarChart categories={deviceCategories} valueLabel="users" />
        </SectionCard>
      </div>
    </div>
  );
}

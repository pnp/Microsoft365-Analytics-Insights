import {
  makeStyles,
  tokens,
  Text,
  Table,
  TableHeader,
  TableHeaderCell,
  TableBody,
  TableRow,
  TableCell,
  MessageBar,
  MessageBarBody,
} from '@fluentui/react-components';
import { KpiGrid } from '../shared/KpiGrid';
import type { KpiDefinition } from '../shared/KpiGrid';
import TimeSeriesChart from '../charts/TimeSeriesChart';
import CategoryBarChart from '../charts/CategoryBarChart';
import DonutChart from '../charts/DonutChart';
import HeatmapChart from '../charts/HeatmapChart';
import { seriesColor } from '../charts/chartCommon';
import type { TeamsMeetings } from '../../types/teamsExplorer';
import {
  SectionCard,
  WindowNote,
  bucketsToCategories,
  formatCount,
  formatDecimal,
  formatHours,
  formatPct,
  queryFor,
  toCategories,
  useTeamsStyles,
} from './teamsShared';

const useStyles = makeStyles({
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  numeric: {
    fontVariantNumeric: 'tabular-nums',
    textAlign: 'right',
  },
});

/**
 * Meetings and calls: how much meeting load there is, what shape it takes, when it happens and
 * whether the experience is any good.
 */
export default function MeetingsPanel({
  data,
  callsAvailable,
}: {
  data: TeamsMeetings;
  /** False when the calls import is off, so every figure here would be a true but useless zero. */
  callsAvailable: boolean;
}) {
  const styles = useStyles();
  const shared = useTeamsStyles();
  const { kpis, quality } = data;

  const workingHours = `${String(data.workingDayStartHour).padStart(2, '0')}:00\u2013${String(
    data.workingDayEndHour,
  ).padStart(2, '0')}:00 UTC`;

  const utcCaveat =
    'Call times are recorded in UTC and the database holds no per-user timezone, so a genuinely '
    + 'global organisation will read high on out-of-hours activity. Compare departments against '
    + 'each other rather than against an absolute target.';

  const kpiItems: KpiDefinition[] = [
    {
      key: 'calls',
      label: 'Calls',
      value: formatCount(kpis.calls),
      hint: `${formatCount(kpis.groupCalls)} group, ${formatCount(kpis.peerToPeerCalls)} one-to-one`,
      info: {
        what: 'Calls and meetings whose records arrived from Microsoft Graph in this period.',
        source: 'Graph call-records change notifications (the Teams calls import).',
      },
    },
    {
      key: 'attendee-hours',
      label: 'Attendee hours',
      value: formatHours(kpis.attendeeHours),
      hint: `${formatHours(kpis.callHours)} of wall-clock meeting time`,
      info: {
        what: 'The total time people spent in meetings, counting each attendee separately.',
        how:
          'This is the real cost of the meeting load: a one-hour meeting with twelve people costs '
          + 'twelve attendee-hours, not one. Wall-clock hours count each meeting once.',
        formula: 'sum(attendee session seconds) / 3600',
        source: 'Graph call records and their per-attendee sessions.',
      },
    },
    {
      key: 'size',
      label: 'Average attendees',
      value: formatDecimal(kpis.meanAttendees),
      hint: `${formatDecimal(kpis.meanDurationMinutes)} minutes on average`,
      info: {
        what: 'The mean number of attendees per call.',
        source: 'Graph call records and their per-attendee sessions.',
      },
    },
    {
      key: 'after-hours',
      label: 'Out of hours',
      value: formatPct(kpis.afterHoursPct),
      hint: `${formatPct(kpis.weekendPct)} at the weekend`,
      tone: kpis.afterHoursPct >= 20 ? 'warning' : 'neutral',
      info: {
        what: `Calls starting outside ${workingHours}, or on a Saturday or Sunday.`,
        how: utcCaveat,
        formula: 'out-of-hours calls / all calls x 100',
        source: 'Graph call records.',
      },
    },
    {
      key: 'concentration',
      label: 'Organiser concentration',
      value: formatPct(kpis.organiserConcentrationPct),
      hint: 'Share of meetings run by the busiest tenth of organisers',
      tone: kpis.organiserConcentrationPct >= 60 ? 'warning' : 'neutral',
      info: {
        what: 'How much of the meeting organisation is carried by a small group.',
        how:
          'With perfectly even contribution this reads 10%. The closer it is to 100%, the more the '
          + 'meeting load depends on a handful of people - normal for a coordination function, a '
          + 'bottleneck anywhere else.',
        formula: 'meetings organised by the top 10% of organisers / all meetings x 100',
        source: 'Graph call records, grouped by organiser.',
      },
    },
    {
      key: 'engagement',
      label: 'Attendee presence',
      value: formatPct(kpis.attendeeEngagementPct),
      hint: 'Share of each call the average attendee was present for',
      tone: kpis.attendeeEngagementPct > 0 && kpis.attendeeEngagementPct < 60 ? 'warning' : 'neutral',
      info: {
        what: 'How much of a call the average attendee actually attends.',
        how:
          'Each attendee session is compared against the length of the call it belongs to, and '
          + 'capped at 100%. A low figure means habitual late joining or early leaving, which is '
          + 'usually a sign meetings are too long or the wrong people are invited.',
        formula: 'mean(attendee session seconds / call seconds), capped at 1',
        source: 'Graph call records and their per-attendee sessions.',
      },
    },
  ];

  const trendSeries = [
    {
      name: 'Calls',
      points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.calls })),
    },
    {
      name: 'Distinct attendees',
      points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.attendees })),
    },
  ];

  const minutesSeries = [
    {
      name: 'Meeting minutes',
      points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.minutes })),
    },
  ];

  const heatCells = data.heatmap.map((cell) => ({
    dayOfWeek: cell.dayOfWeek,
    hour: cell.hour,
    value: cell.calls,
  }));

  if (!callsAvailable) {
    return (
      <div>
        <MessageBar intent="warning">
          <MessageBarBody>
            The Teams calls import is switched off, so there are no call records to analyse. Enable
            it in the installer, grant the runtime app the <strong>CallRecords.Read.All</strong>{' '}
            application permission, and make sure a Service Bus connection is configured - the Graph
            webhook queues every call notification through it, so without Service Bus the endpoint
            returns 503 and nothing is ever imported.
          </MessageBarBody>
        </MessageBar>
      </div>
    );
  }

  return (
    <div>
      <WindowNote window={data.window} includeUsage={false} />

      <div style={{ marginTop: '16px' }}>
        <KpiGrid items={kpiItems} />
      </div>

      <div className={shared.stack}>
        <SectionCard
          title="Meeting volume"
          description="Calls and the number of distinct people in them, each week."
          query={queryFor(data.queries, 'calls-trend')}
          isEmpty={data.trend.length === 0}
        >
          <TimeSeriesChart series={trendSeries} valueLabel="Count" height={240} />
        </SectionCard>

        <SectionCard
          title="Meeting minutes"
          description="Total wall-clock meeting time each week."
          query={queryFor(data.queries, 'calls-trend')}
          isEmpty={data.trend.length === 0}
        >
          <TimeSeriesChart series={minutesSeries} valueLabel="Minutes" height={200} />
        </SectionCard>

        <SectionCard
          title="When meetings happen"
          description="Calls by day of week and hour, in UTC."
          query={queryFor(data.queries, 'calls-heatmap')}
          isEmpty={heatCells.length === 0}
          note={utcCaveat}
        >
          <HeatmapChart
            cells={heatCells}
            valueLabel="calls"
            footnote={`Assumed working day: ${workingHours}.`}
          />
        </SectionCard>
      </div>

      <div className={shared.grid}>
        <SectionCard
          title="Meeting size"
          description="How many people are in the room."
          query={queryFor(data.queries, 'calls-sizes')}
          isEmpty={data.sizeDistribution.every((b) => b.count === 0)}
        >
          <CategoryBarChart
            categories={bucketsToCategories(data.sizeDistribution)}
            valueLabel="calls"
            showShare
          />
        </SectionCard>

        <SectionCard
          title="Meeting length"
          description="How long meetings actually run."
          query={queryFor(data.queries, 'calls-durations')}
          isEmpty={data.durationDistribution.every((b) => b.count === 0)}
        >
          <CategoryBarChart
            categories={bucketsToCategories(data.durationDistribution)}
            valueLabel="calls"
            showShare
          />
        </SectionCard>

        <SectionCard
          title="Time of day"
          description="When the meeting load falls."
          query={queryFor(data.queries, 'calls-heatmap')}
          isEmpty={data.periodOfDay.every((b) => b.count === 0)}
        >
          <CategoryBarChart
            categories={bucketsToCategories(data.periodOfDay)}
            valueLabel="calls"
            showShare
          />
        </SectionCard>

        <SectionCard
          title="Modalities in use"
          description="Are people using more than audio?"
          query={queryFor(data.queries, 'calls-modalities')}
          isEmpty={data.modalityMix.length === 0}
          note={
            'Counted per attendee session, and a session can use several modalities, so these do '
            + 'not sum to the number of calls.'
          }
        >
          <DonutChart
            categories={bucketsToCategories(data.modalityMix)}
            colours={data.modalityMix.map((_, i) => seriesColor(i))}
            centreValue={formatCount(data.modalityMix.reduce((sum, b) => sum + b.count, 0))}
            centreLabel="sessions"
          />
        </SectionCard>

        <SectionCard
          title="Top organisers"
          description="Who runs the meetings."
          query={queryFor(data.queries, 'calls-top-organisers')}
          isEmpty={data.topOrganisers.length === 0}
        >
          <CategoryBarChart categories={toCategories(data.topOrganisers)} valueLabel="meetings" />
        </SectionCard>

        <SectionCard
          title="Top attendees"
          description="Who spends the most time in meetings."
          query={queryFor(data.queries, 'calls-top-attendees')}
          isEmpty={data.topAttendees.length === 0}
        >
          <CategoryBarChart categories={toCategories(data.topAttendees)} valueLabel="meetings" />
        </SectionCard>
      </div>

      <div className={shared.stack}>
        <SectionCard
          title="Call experience"
          description="Feedback users left, and calls that failed."
          query={queryFor(data.queries, 'calls-ratings')}
          isEmpty={quality.feedbackCount === 0 && quality.failureCount === 0}
          emptyMessage={
            'No call feedback or failures were recorded in this period. That is the normal case: '
            + 'Graph only carries feedback a participant actually submitted, and most never do - so '
            + 'this is NOT evidence that call quality was good.'
          }
          note={
            'Absence of feedback is not evidence of quality. Treat these as a floor on problems, '
            + 'never as a measure of the call experience.'
          }
        >
          {quality.feedbackCount > 0 && (
            <div style={{ marginBottom: '16px' }}>
              <Text size={200} className={styles.muted}>
                {formatCount(quality.feedbackCount)} feedback submissions
              </Text>
              <CategoryBarChart
                categories={bucketsToCategories(quality.ratings)}
                valueLabel="submissions"
                showShare
              />
            </div>
          )}

          {quality.failureCount > 0 && (
            <div className={shared.tableWrap}>
              <Text size={200} className={styles.muted}>
                {formatCount(quality.failureCount)} recorded call failures
              </Text>
              <Table size="small" aria-label="Call failure reasons">
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>Reason</TableHeaderCell>
                    <TableHeaderCell>Failures</TableHeaderCell>
                    <TableHeaderCell>Share</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {quality.failureReasons.map((row) => (
                    <TableRow key={row.key}>
                      <TableCell>{row.label}</TableCell>
                      <TableCell className={styles.numeric}>{formatCount(row.count)}</TableCell>
                      <TableCell className={styles.numeric}>{formatPct(row.sharePct)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          )}
        </SectionCard>
      </div>
    </div>
  );
}

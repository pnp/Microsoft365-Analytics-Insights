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
import { serverPlaceholderText } from '../shared/serverPlaceholder';
import type { TeamsMeetings } from '../../types/teamsExplorer';
import { useT, useTNode } from '../../i18n';
import {
  SectionCard,
  WindowNote,
  formatCount,
  formatDecimal,
  formatHours,
  formatPct,
  queryFor,
  toCategories,
  translatedBucketsToCategories,
  translatedCodeBucketsToCategories,
  translatedCodeLabel,
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

const CALL_RECORDS_READ_ALL = 'CallRecords.Read.All';

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
  const t = useT();
  const tNode = useTNode();
  const { kpis, quality } = data;

  const workingHours = t('teamsExplorer.meetings.workingHoursUtc', {
    start: String(data.workingDayStartHour).padStart(2, '0'),
    end: String(data.workingDayEndHour).padStart(2, '0'),
  });

  const utcCaveat =
    t('teamsExplorer.meetings.utcCaveat');

  const kpiItems: KpiDefinition[] = [
    {
      key: 'calls',
      label: t('teamsExplorer.meetings.kpi.calls.label'),
      value: formatCount(kpis.calls),
      hint: t('teamsExplorer.meetings.kpi.calls.hint', {
        group: formatCount(kpis.groupCalls),
        peer: formatCount(kpis.peerToPeerCalls),
      }),
      info: {
        what: t('teamsExplorer.meetings.kpi.calls.what'),
        source: t('teamsExplorer.source.graphCallRecords'),
      },
    },
    {
      key: 'attendee-hours',
      label: t('teamsExplorer.meetings.kpi.attendeeHours.label'),
      value: formatHours(kpis.attendeeHours),
      hint: t('teamsExplorer.meetings.kpi.attendeeHours.hint', { hours: formatHours(kpis.callHours) }),
      info: {
        what: t('teamsExplorer.meetings.kpi.attendeeHours.what'),
        how:
          t('teamsExplorer.meetings.kpi.attendeeHours.how'),
        formula: t('teamsExplorer.meetings.kpi.attendeeHours.formula'),
        source: t('teamsExplorer.meetings.source.graphCallRecordsSessions'),
      },
    },
    {
      key: 'size',
      label: t('teamsExplorer.meetings.kpi.averageAttendees.label'),
      value: formatDecimal(kpis.meanAttendees),
      hint: t('teamsExplorer.meetings.kpi.averageAttendees.hint', {
        minutes: formatDecimal(kpis.meanDurationMinutes),
      }),
      info: {
        what: t('teamsExplorer.meetings.kpi.averageAttendees.what'),
        source: t('teamsExplorer.meetings.source.graphCallRecordsSessions'),
      },
    },
    {
      key: 'after-hours',
      label: t('teamsExplorer.meetings.kpi.outOfHours.label'),
      value: formatPct(kpis.afterHoursPct),
      hint: t('teamsExplorer.meetings.kpi.outOfHours.hint', { pct: formatPct(kpis.weekendPct) }),
      tone: kpis.afterHoursPct >= 20 ? 'warning' : 'neutral',
      info: {
        what: t('teamsExplorer.meetings.kpi.outOfHours.what', { workingHours }),
        how: utcCaveat,
        formula: t('teamsExplorer.meetings.kpi.outOfHours.formula'),
        source: t('teamsExplorer.meetings.source.graphCallRecords'),
      },
    },
    {
      key: 'concentration',
      label: t('teamsExplorer.meetings.kpi.organiserConcentration.label'),
      value: formatPct(kpis.organiserConcentrationPct),
      hint: t('teamsExplorer.meetings.kpi.organiserConcentration.hint'),
      tone: kpis.organiserConcentrationPct >= 60 ? 'warning' : 'neutral',
      info: {
        what: t('teamsExplorer.meetings.kpi.organiserConcentration.what'),
        how:
          t('teamsExplorer.meetings.kpi.organiserConcentration.how'),
        formula: t('teamsExplorer.meetings.kpi.organiserConcentration.formula'),
        source: t('teamsExplorer.meetings.kpi.organiserConcentration.source'),
      },
    },
    {
      key: 'engagement',
      label: t('teamsExplorer.meetings.kpi.attendeePresence.label'),
      value: formatPct(kpis.attendeeEngagementPct),
      hint: t('teamsExplorer.meetings.kpi.attendeePresence.hint'),
      tone: kpis.attendeeEngagementPct > 0 && kpis.attendeeEngagementPct < 60 ? 'warning' : 'neutral',
      info: {
        what: t('teamsExplorer.meetings.kpi.attendeePresence.what'),
        how:
          t('teamsExplorer.meetings.kpi.attendeePresence.how'),
        formula: t('teamsExplorer.meetings.kpi.attendeePresence.formula'),
        source: t('teamsExplorer.meetings.source.graphCallRecordsSessions'),
      },
    },
  ];

  const trendSeries = [
    {
      name: t('teamsExplorer.meetings.series.calls'),
      points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.calls })),
    },
    {
      name: t('teamsExplorer.meetings.series.distinctAttendees'),
      points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.attendees })),
    },
  ];

  const minutesSeries = [
    {
      name: t('teamsExplorer.meetings.series.meetingMinutes'),
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
            {tNode('teamsExplorer.meetings.callsImportOff.permission', {
              permission: <strong>{CALL_RECORDS_READ_ALL}</strong>,
            })}
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
          title={t('teamsExplorer.meetings.meetingVolume.title')}
          description={t('teamsExplorer.meetings.meetingVolume.description')}
          query={queryFor(data.queries, 'calls-trend')}
          isEmpty={data.trend.length === 0}
        >
          <TimeSeriesChart series={trendSeries} valueLabel={t('teamsExplorer.meetings.valueLabel.count')} height={240} />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.meetings.meetingMinutes.title')}
          description={t('teamsExplorer.meetings.meetingMinutes.description')}
          query={queryFor(data.queries, 'calls-trend')}
          isEmpty={data.trend.length === 0}
        >
          <TimeSeriesChart series={minutesSeries} valueLabel={t('teamsExplorer.meetings.valueLabel.minutes')} height={200} />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.meetings.whenMeetingsHappen.title')}
          description={t('teamsExplorer.meetings.whenMeetingsHappen.description')}
          query={queryFor(data.queries, 'calls-heatmap')}
          isEmpty={heatCells.length === 0}
          note={utcCaveat}
        >
          <HeatmapChart
            cells={heatCells}
            valueLabel={t('teamsExplorer.meetings.valueLabel.calls')}
            footnote={t('teamsExplorer.meetings.whenMeetingsHappen.footnote', { workingHours })}
          />
        </SectionCard>
      </div>

      <div className={shared.grid}>
        <SectionCard
          title={t('teamsExplorer.meetings.meetingSize.title')}
          description={t('teamsExplorer.meetings.meetingSize.description')}
          query={queryFor(data.queries, 'calls-sizes')}
          isEmpty={data.sizeDistribution.every((b) => b.count === 0)}
        >
          <CategoryBarChart
            categories={translatedBucketsToCategories(t, 'size', data.sizeDistribution)}
            valueLabel={t('teamsExplorer.meetings.valueLabel.calls')}
            showShare
          />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.meetings.meetingLength.title')}
          description={t('teamsExplorer.meetings.meetingLength.description')}
          query={queryFor(data.queries, 'calls-durations')}
          isEmpty={data.durationDistribution.every((b) => b.count === 0)}
        >
          <CategoryBarChart
            categories={translatedBucketsToCategories(t, 'duration', data.durationDistribution)}
            valueLabel={t('teamsExplorer.meetings.valueLabel.calls')}
            showShare
          />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.meetings.timeOfDay.title')}
          description={t('teamsExplorer.meetings.timeOfDay.description')}
          query={queryFor(data.queries, 'calls-heatmap')}
          isEmpty={data.periodOfDay.every((b) => b.count === 0)}
        >
          <CategoryBarChart
            categories={translatedBucketsToCategories(t, 'period', data.periodOfDay)}
            valueLabel={t('teamsExplorer.meetings.valueLabel.calls')}
            showShare
          />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.meetings.modalities.title')}
          description={t('teamsExplorer.meetings.modalities.description')}
          query={queryFor(data.queries, 'calls-modalities')}
          isEmpty={data.modalityMix.length === 0}
          note={
            t('teamsExplorer.meetings.modalities.note')
          }
        >
          <DonutChart
            categories={translatedCodeBucketsToCategories(t, 'modality', data.modalityMix)}
            colours={data.modalityMix.map((_, i) => seriesColor(i))}
            centreValue={formatCount(data.modalityMix.reduce((sum, b) => sum + b.count, 0))}
            centreLabel={t('teamsExplorer.meetings.modalities.centreLabel')}
          />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.meetings.topOrganisers.title')}
          description={t('teamsExplorer.meetings.topOrganisers.description')}
          query={queryFor(data.queries, 'calls-top-organisers')}
          isEmpty={data.topOrganisers.length === 0}
        >
          <CategoryBarChart categories={toCategories(data.topOrganisers)} valueLabel={t('teamsExplorer.meetings.valueLabel.meetings')} />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.meetings.topAttendees.title')}
          description={t('teamsExplorer.meetings.topAttendees.description')}
          query={queryFor(data.queries, 'calls-top-attendees')}
          isEmpty={data.topAttendees.length === 0}
        >
          <CategoryBarChart categories={toCategories(data.topAttendees)} valueLabel={t('teamsExplorer.meetings.valueLabel.meetings')} />
        </SectionCard>
      </div>

      <div className={shared.stack}>
        <SectionCard
          title={t('teamsExplorer.meetings.callExperience.title')}
          description={t('teamsExplorer.meetings.callExperience.description')}
          query={queryFor(data.queries, 'calls-ratings')}
          isEmpty={quality.feedbackCount === 0 && quality.failureCount === 0}
          emptyMessage={
            t('teamsExplorer.meetings.callExperience.empty')
          }
          note={
            t('teamsExplorer.meetings.callExperience.note')
          }
        >
          {quality.feedbackCount > 0 && (
            <div style={{ marginBottom: '16px' }}>
              <Text size={200} className={styles.muted}>
                {t('teamsExplorer.meetings.callExperience.feedbackSubmissions', {
                  count: formatCount(quality.feedbackCount),
                })}
              </Text>
              <CategoryBarChart
                categories={translatedCodeBucketsToCategories(t, 'quality', quality.ratings)}
                valueLabel={t('teamsExplorer.meetings.valueLabel.submissions')}
                showShare
              />
            </div>
          )}

          {quality.failureCount > 0 && (
            <div className={shared.tableWrap}>
              <Text size={200} className={styles.muted}>
                {t('teamsExplorer.meetings.callExperience.recordedCallFailures', {
                  count: formatCount(quality.failureCount),
                })}
              </Text>
              <Table size="small" aria-label={t('teamsExplorer.meetings.callExperience.failureReasonsAria')}>
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>{t('teamsExplorer.meetings.callExperience.column.reason')}</TableHeaderCell>
                    <TableHeaderCell>{t('teamsExplorer.meetings.callExperience.column.failures')}</TableHeaderCell>
                    <TableHeaderCell>{t('teamsExplorer.meetings.callExperience.column.share')}</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {quality.failureReasons.map((row) => (
                    <TableRow key={row.key}>
                      <TableCell>{serverPlaceholderText(t, translatedCodeLabel(t, 'quality', row.key))}</TableCell>
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

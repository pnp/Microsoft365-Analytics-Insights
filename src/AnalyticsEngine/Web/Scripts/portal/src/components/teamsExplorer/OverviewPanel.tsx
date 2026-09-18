import { makeStyles, tokens, Text, Card, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { KpiGrid } from '../shared/KpiGrid';
import type { KpiDefinition } from '../shared/KpiGrid';
import GaugeRing from '../charts/GaugeRing';
import TimeSeriesChart from '../charts/TimeSeriesChart';
import DonutChart from '../charts/DonutChart';
import { seriesColor } from '../charts/chartCommon';
import type { TeamsOverview } from '../../types/teamsExplorer';
import {
  SectionCard,
  WindowNote,
  formatCount,
  formatDecimal,
  formatHours,
  formatPct,
  queryFor,
  reachTone,
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
  const { kpis } = data;

  const kpiItems: KpiDefinition[] = [
    {
      key: 'reach',
      label: 'Teams reach',
      value: formatPct(kpis.reachPct),
      hint: `${formatCount(kpis.activeUsers)} of ${formatCount(kpis.knownUsers)} known users`,
      tone: reachTone(kpis.reachPct),
      info: {
        what: 'The share of directory users who did anything at all in Teams during the period.',
        how:
          'A user counts as active on a day when the Microsoft 365 usage report shows any chat '
          + 'message, channel post, reply, meeting or call for them. The denominator is every user '
          + 'in the directory whose account is enabled, or whose status is unknown.',
        formula: 'active users / known users x 100',
        source:
          'Microsoft 365 usage reports. This figure differs from the Reports page, which counts '
          + 'users by their last-activity date inside a single report snapshot rather than by '
          + 'measuring each day.',
      },
    },
    {
      key: 'channel-share',
      label: 'Open collaboration',
      value: formatPct(kpis.openCollaborationPct),
      hint: 'Chat messages posted in channels rather than private chats',
      tone: kpis.openCollaborationPct < 15 ? 'warning' : 'neutral',
      info: {
        what: 'Channel messages as a share of all chat messages.',
        how:
          'Channel posts stay visible and searchable for everyone who joins the work later; '
          + 'private chat does not. A very low share means knowledge is accumulating where nobody '
          + 'else can find it.',
        formula: 'channel messages / (channel messages + private chat messages) x 100',
        source: 'Microsoft 365 usage reports (team chat, channel posts and replies vs private chat).',
      },
    },
    {
      key: 'meetings',
      label: 'Meetings per active user',
      value: formatDecimal(kpis.meetingsPerActiveUser),
      hint: `${formatCount(kpis.meetingsAttended)} attended in total`,
      info: {
        what: 'Meetings attended per active user over the period.',
        how:
          'A tenant-wide average hides the teams that are actually saturated, so read it alongside '
          + 'the department breakdown on the Adoption tab.',
        formula: 'meetings attended / active users',
        source: 'Microsoft 365 usage reports.',
      },
    },
    {
      key: 'audio-hours',
      label: 'Audio hours',
      value: formatHours(kpis.audioHours),
      hint: `Video on ${formatPct(kpis.videoSharePct)}, sharing on ${formatPct(kpis.screenShareSharePct)} of those hours`,
      info: {
        what: 'Total hours of audio across Teams calls and meetings.',
        how:
          'Audio is used as the wall-clock figure because audio, video and screenshare durations '
          + 'OVERLAP inside a single meeting - adding them together would produce more hours than '
          + 'the meetings actually lasted. Video and screenshare are therefore reported as a share '
          + 'of audio hours, and each can approach 100%.',
        formula: 'audio seconds / 3600; video share = video seconds / audio seconds x 100',
        source: 'Microsoft 365 usage reports.',
      },
    },
    {
      key: 'calls',
      label: 'Calls recorded',
      value: formatCount(kpis.calls),
      hint: 'From the Graph call-records webhook',
      info: {
        what: 'Calls and meetings whose records arrived from Microsoft Graph.',
        how:
          'Separate from the meeting counts above, which come from the usage reports. Call records '
          + 'arrive within minutes rather than days, which is why the two cover slightly different '
          + 'windows.',
        source: 'Graph call-records change notifications (the Teams calls import).',
      },
    },
    {
      key: 'teams',
      label: 'Active teams',
      value: `${formatCount(kpis.activeTeams)} / ${formatCount(kpis.totalTeams)}`,
      hint: `${formatCount(kpis.activeChannels)} of ${formatCount(kpis.totalChannels)} channels saw a message`,
      info: {
        what: 'Teams with at least one channel message in the period.',
        how:
          'Only teams authorised for deep analytics can be measured, so a team that has never been '
          + 'authorised is counted in the total but can never appear as active. The Teams & '
          + 'channels tab keeps the two apart.',
        source: 'Teams deep analytics (per-team delegated authorisation).',
      },
    },
  ];

  const trendQuery = queryFor(data.queries, 'overview-trend');
  const segmentQuery = queryFor(data.queries, 'overview-segments');

  const activitySeries = [
    {
      name: 'Active users',
      points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.activeUsers })),
    },
  ];

  const messageSeries = [
    {
      name: 'Channel messages',
      points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.channelMessages })),
    },
    {
      name: 'Private chat messages',
      points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.privateMessages })),
    },
    {
      name: 'Meetings attended',
      points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.meetingsAttended })),
    },
  ];

  const segmentCategories = data.segmentMix.map((slice) => ({
    label: slice.label,
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
          {data.judgements.map((judgement) => (
            <Card
              key={judgement.key}
              className={styles.judgement}
              style={{ borderLeftColor: TONE_COLOUR[judgement.tone] ?? TONE_COLOUR.neutral }}
            >
              <Text weight="semibold">{judgement.headline}</Text>
              <Text size={200} className={styles.muted}>
                {judgement.detail}
              </Text>
            </Card>
          ))}
        </div>
      )}

      <div className={shared.grid}>
        <SectionCard
          title="Reach against the directory"
          description="Active users as a share of the directory, on the adoption scale."
          query={queryFor(data.queries, 'overview-usage')}
          isEmpty={kpis.knownUsers === 0}
          emptyMessage="No directory users are known, so reach cannot be measured. Switch on the Graph user metadata import."
        >
          <div className={styles.gaugeRow}>
            <GaugeRing
              value={kpis.reachPct}
              label={formatPct(kpis.reachPct)}
              sublabel={`${formatCount(kpis.activeUsers)} of ${formatCount(kpis.knownUsers)} users`}
            />
          </div>
        </SectionCard>

        <SectionCard
          title="Engagement mix"
          description="How habitually people use Teams across the period's working days."
          query={segmentQuery}
          isEmpty={measuredUsers === 0}
          emptyMessage="No users appeared in the Teams usage reports for this period."
        >
          <DonutChart
            categories={segmentCategories}
            colours={segmentCategories.map((_, i) => seriesColor(i))}
            centreValue={formatCount(measuredUsers)}
            centreLabel="measured users"
          />
          <Text size={200} className={styles.muted}>
            {data.segmentMix.map((slice) => `${slice.label}: ${slice.description}`).join(' ')}
          </Text>
        </SectionCard>
      </div>

      <div className={shared.stack}>
        <SectionCard
          title="Weekly active users"
          description="Distinct people who did anything in Teams each week."
          query={trendQuery}
          isEmpty={data.trend.length === 0}
        >
          <TimeSeriesChart series={activitySeries} valueLabel="Users" height={240} />
        </SectionCard>

        <SectionCard
          title="What people are doing"
          description="Channel messages, private chat and meetings attended each week."
          query={trendQuery}
          isEmpty={data.trend.length === 0}
        >
          <TimeSeriesChart series={messageSeries} valueLabel="Count" height={260} />
        </SectionCard>
      </div>

      {kpis.knownUsers > 0 && kpis.activeUsers === 0 && (
        <MessageBar intent="warning" style={{ marginTop: '16px' }}>
          <MessageBarBody>
            The directory has users but none were active in Teams in this period. That is unusual
            enough to be worth checking the import before drawing any conclusion from it.
          </MessageBarBody>
        </MessageBar>
      )}
    </div>
  );
}

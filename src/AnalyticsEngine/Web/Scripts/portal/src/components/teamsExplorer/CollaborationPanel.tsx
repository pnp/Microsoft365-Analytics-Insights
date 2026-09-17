import {
  makeStyles,
  tokens,
  Text,
  Button,
  Badge,
  Table,
  TableHeader,
  TableHeaderCell,
  TableBody,
  TableRow,
  TableCell,
  MessageBar,
  MessageBarBody,
} from '@fluentui/react-components';
import { ArrowDownload16Regular } from '@fluentui/react-icons';
import { KpiGrid } from '../shared/KpiGrid';
import type { KpiDefinition } from '../shared/KpiGrid';
import CategoryBarChart from '../charts/CategoryBarChart';
import TimeSeriesChart from '../charts/TimeSeriesChart';
import type { TeamsCollaboration } from '../../types/teamsExplorer';
import {
  SENTIMENT_SCALE_NOTE,
  SectionCard,
  WindowNote,
  bucketsToCategories,
  formatCount,
  formatSentiment,
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
  exportRow: {
    display: 'flex',
    justifyContent: 'flex-end',
    marginBottom: '8px',
  },
});

/**
 * Teams and channels: the collaboration structure, and the governance problems hiding in it.
 */
export default function CollaborationPanel({
  data,
  analyticsAvailable,
  authorisedTeams,
  onExportTeams,
  onExportChannels,
  exporting,
}: {
  data: TeamsCollaboration;
  /** False when the Teams deep-analytics import is off. */
  analyticsAvailable: boolean;
  /** Teams with a delegated token. Zero means channel content can never appear. */
  authorisedTeams: number;
  onExportTeams: () => void;
  onExportChannels: () => void;
  exporting: boolean;
}) {
  const styles = useStyles();
  const shared = useTeamsStyles();
  const { kpis } = data;

  const kpiItems: KpiDefinition[] = [
    {
      key: 'teams',
      label: 'Teams',
      value: formatCount(kpis.totalTeams),
      hint: `${formatCount(kpis.authorisedTeams)} authorised for deep analytics`,
      info: {
        what: 'Teams discovered by the group crawl.',
        how:
          'Only authorised teams can have their channel content read, so an unauthorised team is '
          + 'counted here but can never show messages, reactions or sentiment.',
        source: 'Teams deep analytics.',
      },
    },
    {
      key: 'active',
      label: 'Active teams',
      value: formatCount(kpis.activeTeams),
      hint: `${formatCount(kpis.dormantTeams)} authorised teams were silent`,
      tone: kpis.dormantTeams > kpis.activeTeams ? 'warning' : 'good',
      info: {
        what: 'Teams with at least one channel message in the period.',
        how:
          'The dormant count deliberately covers AUTHORISED teams only. An unauthorised team is '
          + 'unmeasured, not quiet, and archiving one on that basis would be a mistake.',
        source: 'Teams deep analytics (daily channel statistics).',
      },
    },
    {
      key: 'ownerless',
      label: 'Ownerless teams',
      value: formatCount(kpis.ownerlessTeams),
      hint: 'Nobody can govern these',
      tone: kpis.ownerlessTeams > 0 ? 'critical' : 'good',
      info: {
        what: 'Teams with no owner recorded at all.',
        how:
          'An ownerless team cannot be governed: nobody can approve membership, manage its files '
          + 'or archive it when the work ends. This is usually the fastest governance win on the '
          + 'page.',
        source: 'Teams deep analytics (team owners).',
      },
    },
    {
      key: 'channels',
      label: 'Active channels',
      value: `${formatCount(kpis.activeChannels)} / ${formatCount(kpis.totalChannels)}`,
      hint: `${formatCount(kpis.channelMessages)} messages`,
      info: {
        what: 'Channels with at least one message in the period, out of all known channels.',
        source: 'Teams deep analytics (daily channel statistics).',
      },
    },
    {
      key: 'reactions',
      label: 'Reactions',
      value: formatCount(kpis.reactions),
      hint: 'Emoji responses to channel posts',
      info: {
        what: 'Reactions left on channel messages during the period.',
        how:
          'A cheap but genuine engagement signal: reactions are what a channel gets when people are '
          + 'reading it but have nothing to add.',
        source: 'Teams deep analytics.',
      },
    },
  ];

  const membershipSeries = [
    {
      name: 'Members seen',
      points: data.membershipTrend.map((p) => ({ weekStart: p.weekStart, value: p.activeUsers })),
    },
  ];

  if (!analyticsAvailable) {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>
          Teams deep analytics is switched off, so teams, channels, tabs, reactions and channel
          statistics are not being imported. Enable <strong>Teams</strong> in the installer, then
          authorise individual teams on the <strong>Teams permissions</strong> page under
          Administration - channel content needs a delegated token per team.
        </MessageBarBody>
      </MessageBar>
    );
  }

  return (
    <div>
      <WindowNote window={data.window} includeUsage={false} />

      {authorisedTeams === 0 && (
        <MessageBar intent="warning" style={{ marginTop: '12px' }}>
          <MessageBarBody>
            No team has been authorised for deep analytics, so no channel messages, reactions or
            sentiment can be read however long the import runs. Authorise teams on the{' '}
            <strong>Teams permissions</strong> page under Administration.
          </MessageBarBody>
        </MessageBar>
      )}

      <div style={{ marginTop: '16px' }}>
        <KpiGrid items={kpiItems} />
      </div>

      <div className={shared.stack}>
        <SectionCard
          title="Team leaderboard"
          description="The teams carrying the most collaboration."
          query={queryFor(data.queries, 'collab-teams')}
          isEmpty={data.teams.length === 0}
          note={SENTIMENT_SCALE_NOTE}
        >
          <div className={styles.exportRow}>
            <Button
              appearance="subtle"
              size="small"
              icon={<ArrowDownload16Regular />}
              onClick={onExportTeams}
              disabled={exporting}
            >
              Export CSV
            </Button>
          </div>
          <div className={shared.tableWrap}>
            <Table size="small" aria-label="Team leaderboard">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>Team</TableHeaderCell>
                  <TableHeaderCell>Members</TableHeaderCell>
                  <TableHeaderCell>Owners</TableHeaderCell>
                  <TableHeaderCell>Channels</TableHeaderCell>
                  <TableHeaderCell>Tabs</TableHeaderCell>
                  <TableHeaderCell>Messages</TableHeaderCell>
                  <TableHeaderCell>Reactions</TableHeaderCell>
                  <TableHeaderCell>Active days</TableHeaderCell>
                  <TableHeaderCell>Sentiment</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.teams.map((team) => (
                  <TableRow key={team.id}>
                    <TableCell>
                      {team.name}{' '}
                      {!team.authorised && (
                        <Badge appearance="tint" color="informative" title="Not authorised for deep analytics, so its content is unmeasured rather than quiet">
                          not measured
                        </Badge>
                      )}
                      {team.owners === 0 && (
                        <Badge appearance="tint" color="danger" title="No owner recorded">
                          ownerless
                        </Badge>
                      )}
                    </TableCell>
                    <TableCell className={styles.numeric}>{formatCount(team.members)}</TableCell>
                    <TableCell className={styles.numeric}>{formatCount(team.owners)}</TableCell>
                    <TableCell className={styles.numeric}>{formatCount(team.channels)}</TableCell>
                    <TableCell className={styles.numeric}>{formatCount(team.tabs)}</TableCell>
                    <TableCell className={styles.numeric}>{formatCount(team.messages)}</TableCell>
                    <TableCell className={styles.numeric}>{formatCount(team.reactions)}</TableCell>
                    <TableCell className={styles.numeric}>{formatCount(team.activeDays)}</TableCell>
                    <TableCell>{formatSentiment(team.sentiment)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </SectionCard>

        <SectionCard
          title="Channel leaderboard"
          description="Which channels are succeeding, and which need encouragement."
          query={queryFor(data.queries, 'collab-channels')}
          isEmpty={data.channels.length === 0}
          note={SENTIMENT_SCALE_NOTE}
        >
          <div className={styles.exportRow}>
            <Button
              appearance="subtle"
              size="small"
              icon={<ArrowDownload16Regular />}
              onClick={onExportChannels}
              disabled={exporting}
            >
              Export CSV
            </Button>
          </div>
          <div className={shared.tableWrap}>
            <Table size="small" aria-label="Channel leaderboard">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>Channel</TableHeaderCell>
                  <TableHeaderCell>Team</TableHeaderCell>
                  <TableHeaderCell>Messages</TableHeaderCell>
                  <TableHeaderCell>People reacting</TableHeaderCell>
                  <TableHeaderCell>Reactions</TableHeaderCell>
                  <TableHeaderCell>Tabs</TableHeaderCell>
                  <TableHeaderCell>Active days</TableHeaderCell>
                  <TableHeaderCell>Sentiment</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.channels.map((channel) => (
                  <TableRow key={channel.id}>
                    <TableCell>{channel.name}</TableCell>
                    <TableCell>{channel.teamName}</TableCell>
                    <TableCell className={styles.numeric}>{formatCount(channel.messages)}</TableCell>
                    <TableCell className={styles.numeric}>{formatCount(channel.reactingUsers)}</TableCell>
                    <TableCell className={styles.numeric}>{formatCount(channel.reactions)}</TableCell>
                    <TableCell className={styles.numeric}>{formatCount(channel.tabs)}</TableCell>
                    <TableCell className={styles.numeric}>{formatCount(channel.activeDays)}</TableCell>
                    <TableCell>{formatSentiment(channel.sentiment)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </SectionCard>
      </div>

      <div className={shared.grid}>
        <SectionCard
          title="Teams with no owner"
          description="Ordered by how long they have been ungoverned."
          query={queryFor(data.queries, 'collab-ownerless')}
          isEmpty={data.ownerlessTeams.length === 0}
          emptyMessage="Every team has at least one owner."
        >
          <div className={shared.tableWrap}>
            <Table size="small" aria-label="Teams with no owner">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>Team</TableHeaderCell>
                  <TableHeaderCell>Days since discovered</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.ownerlessTeams.map((row) => (
                  <TableRow key={row.name}>
                    <TableCell>{row.name}</TableCell>
                    <TableCell className={styles.numeric}>{formatCount(row.count)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </SectionCard>

        <SectionCard
          title="Dormant teams"
          description="Authorised teams with no channel message in the period."
          query={queryFor(data.queries, 'collab-dormant')}
          isEmpty={data.dormantTeams.length === 0}
          emptyMessage="Every authorised team saw at least one message."
          note={
            'Only authorised teams appear here. An unauthorised team is unmeasured, not quiet, so '
            + 'listing it as dormant would be misleading.'
          }
        >
          <div className={shared.tableWrap}>
            <Table size="small" aria-label="Dormant teams">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>Team</TableHeaderCell>
                  <TableHeaderCell>Channels</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.dormantTeams.map((row) => (
                  <TableRow key={row.name}>
                    <TableCell>{row.name}</TableCell>
                    <TableCell className={styles.numeric}>{formatCount(row.count)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </SectionCard>

        <SectionCard
          title="Apps and tabs in use"
          description="Is Teams a workspace, or just a chat app?"
          query={queryFor(data.queries, 'collab-tabs')}
          isEmpty={data.tabUsage.length === 0}
        >
          <CategoryBarChart categories={toCategories(data.tabUsage)} valueLabel="channels" />
        </SectionCard>

        <SectionCard
          title="Reactions used"
          description="How people respond when they have nothing to add."
          query={queryFor(data.queries, 'collab-reactions')}
          isEmpty={data.reactionMix.length === 0}
        >
          <CategoryBarChart
            categories={bucketsToCategories(data.reactionMix)}
            valueLabel="reactions"
            showShare
          />
        </SectionCard>
      </div>

      <div className={shared.stack}>
        <SectionCard
          title="Membership activity"
          description="Distinct people seen in team membership each week."
          query={queryFor(data.queries, 'collab-membership')}
          isEmpty={data.membershipTrend.length === 0}
        >
          <TimeSeriesChart series={membershipSeries} valueLabel="Members" height={200} />
        </SectionCard>
      </div>

      <Text size={200} className={styles.muted} style={{ marginTop: '12px', display: 'block' }}>
        Team membership is read from the most recent membership snapshot per team, not from the whole
        history - otherwise every person who has ever been a member would be counted.
      </Text>
    </div>
  );
}

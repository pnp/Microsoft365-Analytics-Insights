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
import SentimentLight from '../shared/SentimentLight';
import type { TeamsCollaboration } from '../../types/teamsExplorer';
import { useT, useTNode } from '../../i18n';
import {
  SectionCard,
  WindowNote,
  bucketsToCategories,
  formatCount,
  queryFor,
  sentimentScaleNote,
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
  const t = useT();
  const tNode = useTNode();
  const sentimentScaleNoteText = sentimentScaleNote(t);
  const { kpis } = data;

  const kpiItems: KpiDefinition[] = [
    {
      key: 'teams',
      label: t('teamsExplorer.collaboration.kpi.teams.label'),
      value: formatCount(kpis.totalTeams),
      hint: t('teamsExplorer.collaboration.kpi.teams.hint', { count: formatCount(kpis.authorisedTeams) }),
      info: {
        what: t('teamsExplorer.collaboration.kpi.teams.what'),
        how:
          t('teamsExplorer.collaboration.kpi.teams.how'),
        source: t('teamsExplorer.collaboration.source.teamsDeepAnalytics'),
      },
    },
    {
      key: 'active',
      label: t('teamsExplorer.collaboration.kpi.activeTeams.label'),
      value: formatCount(kpis.activeTeams),
      hint: t('teamsExplorer.collaboration.kpi.activeTeams.hint', { count: formatCount(kpis.dormantTeams) }),
      tone: kpis.dormantTeams > kpis.activeTeams ? 'warning' : 'good',
      info: {
        what: t('teamsExplorer.collaboration.kpi.activeTeams.what'),
        how:
          t('teamsExplorer.collaboration.kpi.activeTeams.how'),
        source: t('teamsExplorer.collaboration.source.dailyChannelStatistics'),
      },
    },
    {
      key: 'ownerless',
      label: t('teamsExplorer.collaboration.kpi.ownerlessTeams.label'),
      value: formatCount(kpis.ownerlessTeams),
      hint: t('teamsExplorer.collaboration.kpi.ownerlessTeams.hint'),
      tone: kpis.ownerlessTeams > 0 ? 'critical' : 'good',
      info: {
        what: t('teamsExplorer.collaboration.kpi.ownerlessTeams.what'),
        how:
          t('teamsExplorer.collaboration.kpi.ownerlessTeams.how'),
        source: t('teamsExplorer.collaboration.kpi.ownerlessTeams.source'),
      },
    },
    {
      key: 'channels',
      label: t('teamsExplorer.collaboration.kpi.activeChannels.label'),
      value: `${formatCount(kpis.activeChannels)} / ${formatCount(kpis.totalChannels)}`,
      hint: t('teamsExplorer.collaboration.kpi.activeChannels.hint', { count: formatCount(kpis.channelMessages) }),
      info: {
        what: t('teamsExplorer.collaboration.kpi.activeChannels.what'),
        source: t('teamsExplorer.collaboration.source.dailyChannelStatistics'),
      },
    },
    {
      key: 'reactions',
      label: t('teamsExplorer.collaboration.kpi.reactions.label'),
      value: formatCount(kpis.reactions),
      hint: t('teamsExplorer.collaboration.kpi.reactions.hint'),
      info: {
        what: t('teamsExplorer.collaboration.kpi.reactions.what'),
        how:
          t('teamsExplorer.collaboration.kpi.reactions.how'),
        source: t('teamsExplorer.collaboration.source.teamsDeepAnalytics'),
      },
    },
  ];

  const membershipSeries = [
    {
      name: t('teamsExplorer.collaboration.series.membersSeen'),
      points: data.membershipTrend.map((p) => ({ weekStart: p.weekStart, value: p.activeUsers })),
    },
  ];

  if (!analyticsAvailable) {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>
          {tNode('teamsExplorer.collaboration.analyticsOff', {
            setting: <strong>{t('teamsExplorer.collaboration.analyticsOff.teamsToggle')}</strong>,
            page: <strong>{t('teamsExplorer.collaboration.analyticsOff.teamsPermissions')}</strong>,
          })}
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
            {tNode('teamsExplorer.collaboration.noAuthorisedTeams', {
              page: <strong>{t('teamsExplorer.collaboration.analyticsOff.teamsPermissions')}</strong>,
            })}
          </MessageBarBody>
        </MessageBar>
      )}

      <div style={{ marginTop: '16px' }}>
        <KpiGrid items={kpiItems} />
      </div>

      <div className={shared.stack}>
        <SectionCard
          title={t('teamsExplorer.collaboration.teamLeaderboard.title')}
          description={t('teamsExplorer.collaboration.teamLeaderboard.description')}
          query={queryFor(data.queries, 'collab-teams')}
          isEmpty={data.teams.length === 0}
          note={sentimentScaleNoteText}
        >
          <div className={styles.exportRow}>
            <Button
              appearance="subtle"
              size="small"
              icon={<ArrowDownload16Regular />}
              onClick={onExportTeams}
              disabled={exporting}
            >
              {t('teamsExplorer.action.exportCsv')}
            </Button>
          </div>
          <div className={shared.tableWrap}>
            <Table size="small" aria-label={t('teamsExplorer.collaboration.teamLeaderboard.aria')}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>{t('teamsExplorer.column.team')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.members')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.owners')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.channels')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.tabs')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.messages')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.reactions')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.activeDays')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.sentiment')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.teams.map((team) => (
                  <TableRow key={team.id}>
                    <TableCell>
                      {team.name}{' '}
                      {!team.authorised && (
                        <Badge appearance="tint" color="informative" title={t('teamsExplorer.collaboration.badge.notAuthorisedTitle')}>
                          {t('teamsExplorer.collaboration.badge.notMeasured')}
                        </Badge>
                      )}
                      {team.owners === 0 && (
                        <Badge appearance="tint" color="danger" title={t('teamsExplorer.collaboration.badge.noOwnerTitle')}>
                          {t('teamsExplorer.collaboration.badge.ownerless')}
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
                    <TableCell><SentimentLight value={team.sentiment} /></TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.collaboration.channelLeaderboard.title')}
          description={t('teamsExplorer.collaboration.channelLeaderboard.description')}
          query={queryFor(data.queries, 'collab-channels')}
          isEmpty={data.channels.length === 0}
          note={sentimentScaleNoteText}
        >
          <div className={styles.exportRow}>
            <Button
              appearance="subtle"
              size="small"
              icon={<ArrowDownload16Regular />}
              onClick={onExportChannels}
              disabled={exporting}
            >
              {t('teamsExplorer.action.exportCsv')}
            </Button>
          </div>
          <div className={shared.tableWrap}>
            <Table size="small" aria-label={t('teamsExplorer.collaboration.channelLeaderboard.aria')}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>{t('teamsExplorer.column.channel')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.team')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.messages')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.peopleReacting')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.reactions')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.tabs')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.activeDays')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.sentiment')}</TableHeaderCell>
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
                    <TableCell><SentimentLight value={channel.sentiment} /></TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </SectionCard>
      </div>

      <div className={shared.grid}>
        <SectionCard
          title={t('teamsExplorer.collaboration.ownerless.title')}
          description={t('teamsExplorer.collaboration.ownerless.description')}
          query={queryFor(data.queries, 'collab-ownerless')}
          isEmpty={data.ownerlessTeams.length === 0}
          emptyMessage={t('teamsExplorer.collaboration.ownerless.empty')}
        >
          <div className={shared.tableWrap}>
            <Table size="small" aria-label={t('teamsExplorer.collaboration.ownerless.aria')}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>{t('teamsExplorer.column.team')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.daysSinceDiscovered')}</TableHeaderCell>
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
          title={t('teamsExplorer.collaboration.dormant.title')}
          description={t('teamsExplorer.collaboration.dormant.description')}
          query={queryFor(data.queries, 'collab-dormant')}
          isEmpty={data.dormantTeams.length === 0}
          emptyMessage={t('teamsExplorer.collaboration.dormant.empty')}
          note={
            t('teamsExplorer.collaboration.dormant.note')
          }
        >
          <div className={shared.tableWrap}>
            <Table size="small" aria-label={t('teamsExplorer.collaboration.dormant.aria')}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>{t('teamsExplorer.column.team')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.channels')}</TableHeaderCell>
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
          title={t('teamsExplorer.collaboration.appsTabs.title')}
          description={t('teamsExplorer.collaboration.appsTabs.description')}
          query={queryFor(data.queries, 'collab-tabs')}
          isEmpty={data.tabUsage.length === 0}
        >
          <CategoryBarChart categories={toCategories(data.tabUsage)} valueLabel={t('teamsExplorer.valueLabel.channels')} />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.collaboration.reactionsUsed.title')}
          description={t('teamsExplorer.collaboration.reactionsUsed.description')}
          query={queryFor(data.queries, 'collab-reactions')}
          isEmpty={data.reactionMix.length === 0}
        >
          <CategoryBarChart
            categories={bucketsToCategories(data.reactionMix)}
            valueLabel={t('teamsExplorer.valueLabel.reactions')}
            showShare
          />
        </SectionCard>
      </div>

      <div className={shared.stack}>
        <SectionCard
          title={t('teamsExplorer.collaboration.membership.title')}
          description={t('teamsExplorer.collaboration.membership.description')}
          query={queryFor(data.queries, 'collab-membership')}
          isEmpty={data.membershipTrend.length === 0}
        >
          <TimeSeriesChart series={membershipSeries} valueLabel={t('teamsExplorer.valueLabel.members')} height={200} />
        </SectionCard>
      </div>

      <Text size={200} className={styles.muted} style={{ marginTop: '12px', display: 'block' }}>
        {t('teamsExplorer.collaboration.membershipSnapshotNote')}
      </Text>
    </div>
  );
}

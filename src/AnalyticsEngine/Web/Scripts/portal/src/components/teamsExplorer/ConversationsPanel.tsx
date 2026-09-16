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
import WordCloud from '../charts/WordCloud';
import CategoryBarChart from '../charts/CategoryBarChart';
import TimeSeriesChart from '../charts/TimeSeriesChart';
import type { TeamsConversations } from '../../types/teamsExplorer';
import {
  SENTIMENT_SCALE_NOTE,
  SectionCard,
  WindowNote,
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
});

/**
 * What people are talking about, and how they sound doing it.
 *
 * Everything here depends on cognitive enrichment, which is separately configured from every other
 * import - so the tab distinguishes three states that all render as an empty chart otherwise:
 * cognitive services are not configured, they are configured but nothing has been scored yet, and
 * there genuinely was no conversation.
 */
export default function ConversationsPanel({ data }: { data: TeamsConversations }) {
  const styles = useStyles();
  const shared = useTeamsStyles();

  if (!data.cognitiveAvailable) {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>
          Cognitive services are not configured, so channel messages are never scored: key phrases,
          detected languages and sentiment cannot be produced. Set <strong>CognitiveEndpoint</strong>{' '}
          (with either a key or the runtime service principal) on the web application and the
          importer, then wait for the next Teams import cycle.
        </MessageBarBody>
      </MessageBar>
    );
  }

  const nothingScored = data.scoredChannelDays === 0;

  const sentimentSeries = [
    {
      name: 'Sentiment',
      points: data.sentimentTrend.map((p) => ({ weekStart: p.weekStart, value: p.sentiment })),
    },
  ];

  return (
    <div>
      <WindowNote window={data.window} includeUsage={false} />

      {nothingScored && (
        <MessageBar intent="info" style={{ marginTop: '12px' }}>
          <MessageBarBody>
            Cognitive services are configured, but no channel day in this period carries a score yet.
            Scoring happens during the Teams import, and only for teams that have been authorised for
            deep analytics - so check the Teams permissions page and give the importer a cycle to
            catch up.
          </MessageBarBody>
        </MessageBar>
      )}

      <div className={shared.stack}>
        <SectionCard
          title="What people are talking about"
          description="The key phrases extracted from channel conversation."
          query={queryFor(data.queries, 'conv-keywords')}
          isEmpty={data.keywords.length === 0}
          emptyMessage="No key phrases were extracted for this period."
        >
          <WordCloud categories={toCategories(data.keywords)} valueLabel="mentions" />
        </SectionCard>

        <SectionCard
          title="Sentiment over time"
          description="Weighted by message count, week by week."
          query={queryFor(data.queries, 'conv-sentiment-trend')}
          isEmpty={data.sentimentTrend.every((p) => p.sentiment === null)}
          emptyMessage="No scored conversation in this period."
          note={SENTIMENT_SCALE_NOTE}
        >
          <TimeSeriesChart series={sentimentSeries} valueLabel="Sentiment" height={220} />
        </SectionCard>
      </div>

      <div className={shared.grid}>
        <SectionCard
          title="Languages in use"
          description="What the organisation actually writes in."
          query={queryFor(data.queries, 'conv-languages')}
          isEmpty={data.languages.length === 0}
        >
          <CategoryBarChart categories={toCategories(data.languages)} valueLabel="channel days" showShare />
        </SectionCard>

        <SectionCard
          title="Sentiment by team"
          description="Where the conversation is positive, and where it is not."
          query={queryFor(data.queries, 'conv-sentiment-team')}
          isEmpty={data.sentimentByTeam.length === 0}
          note={SENTIMENT_SCALE_NOTE}
        >
          <div className={shared.tableWrap}>
            <Table size="small" aria-label="Sentiment by team">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>Team</TableHeaderCell>
                  <TableHeaderCell>Messages</TableHeaderCell>
                  <TableHeaderCell>Sentiment</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.sentimentByTeam.map((row) => (
                  <TableRow key={row.name}>
                    <TableCell>{row.name}</TableCell>
                    <TableCell className={styles.numeric}>{formatCount(row.messages)}</TableCell>
                    <TableCell>{formatSentiment(row.sentiment)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </SectionCard>

        <SectionCard
          title="Sentiment by channel"
          description="The same read, one level down."
          query={queryFor(data.queries, 'conv-sentiment-channel')}
          isEmpty={data.sentimentByChannel.length === 0}
          note={SENTIMENT_SCALE_NOTE}
        >
          <div className={shared.tableWrap}>
            <Table size="small" aria-label="Sentiment by channel">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>Channel</TableHeaderCell>
                  <TableHeaderCell>Messages</TableHeaderCell>
                  <TableHeaderCell>Sentiment</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.sentimentByChannel.map((row) => (
                  <TableRow key={row.name}>
                    <TableCell>{row.name}</TableCell>
                    <TableCell className={styles.numeric}>{formatCount(row.messages)}</TableCell>
                    <TableCell>{formatSentiment(row.sentiment)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </SectionCard>
      </div>

      <Text size={200} className={styles.muted} style={{ marginTop: '12px', display: 'block' }}>
        {formatCount(data.scoredChannelDays)} channel-days in this period carry a sentiment score.
        Scoring only covers teams authorised for deep analytics.
      </Text>
    </div>
  );
}

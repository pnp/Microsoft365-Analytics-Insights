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
import SentimentLight from '../shared/SentimentLight';
import type { TeamsConversations } from '../../types/teamsExplorer';
import { useT } from '../../i18n';
import {
  SENTIMENT_SCALE_NOTE,
  SectionCard,
  WindowNote,
  formatCount,
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

const COGNITIVE_ENDPOINT = 'CognitiveEndpoint';

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
  const t = useT();

  if (!data.cognitiveAvailable) {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>
          {t('teamsExplorer.conversations.cognitiveOff.beforeEndpoint')}{' '}
          <strong>{COGNITIVE_ENDPOINT}</strong>{' '}
          {t('teamsExplorer.conversations.cognitiveOff.afterEndpoint')}
        </MessageBarBody>
      </MessageBar>
    );
  }

  const nothingScored = data.scoredChannelDays === 0;

  const sentimentSeries = [
    {
      name: t('teamsExplorer.column.sentiment'),
      points: data.sentimentTrend.map((p) => ({ weekStart: p.weekStart, value: p.sentiment })),
    },
  ];

  return (
    <div>
      <WindowNote window={data.window} includeUsage={false} />

      {nothingScored && (
        <MessageBar intent="info" style={{ marginTop: '12px' }}>
          <MessageBarBody>
            {t('teamsExplorer.conversations.nothingScored')}
          </MessageBarBody>
        </MessageBar>
      )}

      <div className={shared.stack}>
        <SectionCard
          title={t('teamsExplorer.conversations.keywords.title')}
          description={t('teamsExplorer.conversations.keywords.description')}
          query={queryFor(data.queries, 'conv-keywords')}
          isEmpty={data.keywords.length === 0}
          emptyMessage={t('teamsExplorer.conversations.keywords.empty')}
        >
          <WordCloud categories={toCategories(data.keywords)} valueLabel={t('teamsExplorer.valueLabel.mentions')} />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.conversations.sentimentOverTime.title')}
          description={t('teamsExplorer.conversations.sentimentOverTime.description')}
          query={queryFor(data.queries, 'conv-sentiment-trend')}
          isEmpty={data.sentimentTrend.every((p) => p.sentiment === null)}
          emptyMessage={t('teamsExplorer.conversations.sentimentOverTime.empty')}
          note={SENTIMENT_SCALE_NOTE}
        >
          <TimeSeriesChart series={sentimentSeries} valueLabel={t('teamsExplorer.column.sentiment')} height={220} />
        </SectionCard>
      </div>

      <div className={shared.grid}>
        <SectionCard
          title={t('teamsExplorer.conversations.languages.title')}
          description={t('teamsExplorer.conversations.languages.description')}
          query={queryFor(data.queries, 'conv-languages')}
          isEmpty={data.languages.length === 0}
        >
          <CategoryBarChart categories={toCategories(data.languages)} valueLabel={t('teamsExplorer.valueLabel.channelDays')} showShare />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.conversations.sentimentByTeam.title')}
          description={t('teamsExplorer.conversations.sentimentByTeam.description')}
          query={queryFor(data.queries, 'conv-sentiment-team')}
          isEmpty={data.sentimentByTeam.length === 0}
          note={SENTIMENT_SCALE_NOTE}
        >
          <div className={shared.tableWrap}>
            <Table size="small" aria-label={t('teamsExplorer.conversations.sentimentByTeam.aria')}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>{t('teamsExplorer.column.team')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.messages')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.sentiment')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.sentimentByTeam.map((row) => (
                  <TableRow key={row.name}>
                    <TableCell>{row.name}</TableCell>
                    <TableCell className={styles.numeric}>{formatCount(row.messages)}</TableCell>
                    <TableCell><SentimentLight value={row.sentiment} /></TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.conversations.sentimentByChannel.title')}
          description={t('teamsExplorer.conversations.sentimentByChannel.description')}
          query={queryFor(data.queries, 'conv-sentiment-channel')}
          isEmpty={data.sentimentByChannel.length === 0}
          note={SENTIMENT_SCALE_NOTE}
        >
          <div className={shared.tableWrap}>
            <Table size="small" aria-label={t('teamsExplorer.conversations.sentimentByChannel.aria')}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>{t('teamsExplorer.column.channel')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.messages')}</TableHeaderCell>
                  <TableHeaderCell>{t('teamsExplorer.column.sentiment')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.sentimentByChannel.map((row) => (
                  <TableRow key={row.name}>
                    <TableCell>{row.name}</TableCell>
                    <TableCell className={styles.numeric}>{formatCount(row.messages)}</TableCell>
                    <TableCell><SentimentLight value={row.sentiment} /></TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </SectionCard>
      </div>

      <Text size={200} className={styles.muted} style={{ marginTop: '12px', display: 'block' }}>
        {t('teamsExplorer.conversations.scoredChannelDays', {
          count: formatCount(data.scoredChannelDays),
        })}
      </Text>
    </div>
  );
}

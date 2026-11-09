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
import { useT, useTNode } from '../../i18n';
import {
  SectionCard,
  WindowNote,
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
  const tNode = useTNode();
  const sentimentScaleNoteText = sentimentScaleNote(t);

  if (!data.cognitiveAvailable) {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>
          {tNode('teamsExplorer.conversations.cognitiveOff.endpoint', {
            endpoint: <strong>{COGNITIVE_ENDPOINT}</strong>,
          })}
        </MessageBarBody>
      </MessageBar>
    );
  }

  const nothingScored = data.scoredChannelDays === 0;

  // Without See PII, key phrases and languages come back withheld (nothing stored counts the people behind them), and the
  // sentiment of teams of too few members is left out. Each card says so instead of reading as "nothing was said".
  const minimumGroupSize = data.minimumGroupSize ?? null;
  const minimumText = formatCount(minimumGroupSize ?? 5);
  const keywordsWithheld = data.keywordsWithheld === true;
  const languagesWithheld = data.languagesWithheld === true;
  const sentimentByTeamNote =
    minimumGroupSize != null
      ? `${sentimentScaleNoteText} ${t('teamsExplorer.conversations.sentimentByTeam.withheldNote', { count: minimumText })}`
      : sentimentScaleNoteText;
  const sentimentByChannelNote =
    minimumGroupSize != null
      ? `${sentimentScaleNoteText} ${t('teamsExplorer.conversations.sentimentByChannel.withheldNote', { count: minimumText })}`
      : sentimentScaleNoteText;

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
          isEmpty={keywordsWithheld || data.keywords.length === 0}
          emptyMessage={
            keywordsWithheld
              ? t('teamsExplorer.conversations.keywords.withheld')
              : t('teamsExplorer.conversations.keywords.empty')
          }
        >
          <WordCloud categories={toCategories(data.keywords)} valueLabel={t('teamsExplorer.valueLabel.mentions')} />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.conversations.sentimentOverTime.title')}
          description={t('teamsExplorer.conversations.sentimentOverTime.description')}
          query={queryFor(data.queries, 'conv-sentiment-trend')}
          isEmpty={data.sentimentTrend.every((p) => p.sentiment === null)}
          emptyMessage={t('teamsExplorer.conversations.sentimentOverTime.empty')}
          note={sentimentScaleNoteText}
        >
          <TimeSeriesChart series={sentimentSeries} valueLabel={t('teamsExplorer.column.sentiment')} height={220} />
        </SectionCard>
      </div>

      <div className={shared.grid}>
        <SectionCard
          title={t('teamsExplorer.conversations.languages.title')}
          description={t('teamsExplorer.conversations.languages.description')}
          query={queryFor(data.queries, 'conv-languages')}
          isEmpty={languagesWithheld || data.languages.length === 0}
          emptyMessage={languagesWithheld ? t('teamsExplorer.conversations.languages.withheld') : undefined}
        >
          <CategoryBarChart categories={toCategories(data.languages)} valueLabel={t('teamsExplorer.valueLabel.channelDays')} showShare />
        </SectionCard>

        <SectionCard
          title={t('teamsExplorer.conversations.sentimentByTeam.title')}
          description={t('teamsExplorer.conversations.sentimentByTeam.description')}
          query={queryFor(data.queries, 'conv-sentiment-team')}
          isEmpty={data.sentimentByTeam.length === 0}
          note={sentimentByTeamNote}
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
          note={sentimentByChannelNote}
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

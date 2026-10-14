import { useCallback, useEffect, useState } from 'react';
import {
  Badge, Button, Card, MessageBar, MessageBarActions, MessageBarBody, Subtitle2, Table, TableBody, TableCell,
  TableHeader, TableHeaderCell, TableRow, Text, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components';
import { ArrowClockwise16Regular } from '@fluentui/react-icons';
import { fetchPromptCategoryRuns, type PromptCategoryRun } from '../api/promptCategoriesApi';
import { activeLocale, formatDateParts, formatNumber, useT, type TranslationKey } from '../i18n';
import Spinner from './Spinner';
import { promptCategoryErrorText } from './promptCategoryErrors';

type Tone = 'success' | 'informative' | 'warning' | 'danger';
const REASONS: Record<string, { key: TranslationKey; tone: Tone }> = {
  disabled: { key: 'promptCategories.status.disabled', tone: 'informative' },
  enabled: { key: 'promptCategories.status.enabled', tone: 'success' },
  'not-configured': { key: 'promptCategories.status.notConfigured', tone: 'warning' },
  'configuration-unavailable': { key: 'promptCategories.status.configurationUnavailable', tone: 'warning' },
  throttled: { key: 'promptCategories.status.throttled', tone: 'warning' },
  'access-denied': { key: 'promptCategories.status.accessDenied', tone: 'danger' },
  'service-failure': { key: 'promptCategories.status.serviceFailure', tone: 'danger' },
  timeout: { key: 'promptCategories.status.timeout', tone: 'danger' },
  refused: { key: 'promptCategories.status.refused', tone: 'warning' },
  'invalid-response': { key: 'promptCategories.status.invalidResponse', tone: 'danger' },
  'storage-failure': { key: 'promptCategories.status.storageFailure', tone: 'danger' },
};
const UNKNOWN_REASON = REASONS['service-failure'];

const useStyles = makeStyles({
  card: { display: 'flex', flexDirection: 'column', gap: '12px' },
  head: { display: 'flex', justifyContent: 'space-between', alignItems: 'start', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  scroll: { overflowX: 'auto' },
  num: { textAlign: 'end', fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' },
  when: { display: 'flex', flexDirection: 'column', whiteSpace: 'nowrap' },
  status: { minWidth: '200px', maxWidth: '300px' },
  badge: { height: 'auto', minHeight: '20px', whiteSpace: 'normal', textAlign: 'start', paddingBlock: '2px' },
  th: { whiteSpace: 'normal', paddingInlineStart: '12px', verticalAlign: 'bottom' },
  table: { tableLayout: 'auto', minWidth: '960px' },
  empty: { padding: '24px', textAlign: 'center', color: tokens.colorNeutralForeground3 },
});

/** "3 hours ago" in the active language; the exact time sits beneath it. */
function relativeTime(then: Date, now = Date.now()): string {
  const seconds = Math.round((then.getTime() - now) / 1000);
  const formatter = new Intl.RelativeTimeFormat(activeLocale(), { numeric: 'auto' });
  const steps: [Intl.RelativeTimeFormatUnit, number][] = [['day', 86400], ['hour', 3600], ['minute', 60]];
  for (const [unit, size] of steps) if (Math.abs(seconds) >= size) return formatter.format(Math.round(seconds / size), unit);
  return formatter.format(seconds, 'second');
}

export default function PromptCategoryRuns({ active, storageAvailable }: { active: boolean; storageAvailable?: boolean }) {
  const t = useT();
  const styles = useStyles();
  const [runs, setRuns] = useState<PromptCategoryRun[]>([]);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(active);
  const fetchable = active && storageAvailable !== false;

  const load = useCallback(() => {
    let live = true;
    setLoading(true);
    setError(null);
    fetchPromptCategoryRuns().then(value => { if (live) setRuns(value); })
      .catch(e => { if (live) setError(e); })
      .finally(() => { if (live) setLoading(false); });
    return () => { live = false; };
  }, []);
  useEffect(() => (fetchable ? load() : undefined), [fetchable, load]);

  const headers: TranslationKey[] = ['promptCategories.started', 'promptCategories.reason', 'promptCategories.sent',
    'promptCategories.classified', 'promptCategories.other', 'promptCategories.notClassified', 'promptCategories.capped',
    'promptCategories.failed', 'promptCategories.tokens', 'promptCategories.httpAttempts'];

  return <Card className={styles.card}>
    <div className={styles.head}>
      <div>
        <Subtitle2>{t('promptCategories.runs')}</Subtitle2>
        <div><Text size={200} className={styles.muted}>{t('promptCategories.runsHint')}</Text></div>
      </div>
      {fetchable && <Button size="small" icon={<ArrowClockwise16Regular />} disabled={loading} onClick={() => void load()}>{t('promptCategories.retry')}</Button>}
    </div>
    {storageAvailable === false && <Text className={styles.muted}>{t('promptCategories.runsStorageMissing')}</Text>}
    {loading && fetchable && <Spinner size={24} label={t('promptCategories.runsLoading')} />}
    {error !== null && !loading && <MessageBar intent="error" layout="multiline">
      <MessageBarBody>{promptCategoryErrorText(error, t)}</MessageBarBody>
      <MessageBarActions><Button size="small" onClick={() => void load()}>{t('promptCategories.retry')}</Button></MessageBarActions>
    </MessageBar>}
    {fetchable && !loading && error === null && runs.length === 0 &&
      <div className={styles.empty}><Text>{t('promptCategories.runsEmpty')}</Text></div>}
    {runs.length > 0 && <div className={styles.scroll}>
      <Table size="small" className={styles.table} aria-label={t('promptCategories.runs')}>
        <TableHeader><TableRow>
          {headers.map((h, i) => <TableHeaderCell key={h} className={mergeClasses(styles.th, i >= 2 ? styles.num : undefined)}>{t(h)}</TableHeaderCell>)}
        </TableRow></TableHeader>
        <TableBody>
          {runs.map((run, index) => {
            const started = new Date(run.startedUtc);
            const reason = REASONS[run.counters.reason] ?? UNKNOWN_REASON;
            const c = run.counters;
            return <TableRow key={`${run.startedUtc}-${index}`}>
              <TableCell><div className={styles.when}>
                <Text weight="semibold">{relativeTime(started)}</Text>
                <Text size={200} className={styles.muted}>{formatDateParts(started, { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' })}</Text>
              </div></TableCell>
              <TableCell className={styles.status}><Badge className={styles.badge} appearance="tint" color={reason.tone} shape="rounded">{t(reason.key)}</Badge></TableCell>
              <TableCell className={styles.num}>{formatNumber(c.sent)}</TableCell>
              <TableCell className={styles.num}>{formatNumber(c.classified)}</TableCell>
              <TableCell className={styles.num}>{formatNumber(c.other)}</TableCell>
              <TableCell className={styles.num}>{formatNumber(c.notClassified)}</TableCell>
              <TableCell className={styles.num}>{formatNumber(c.capped)}</TableCell>
              <TableCell className={styles.num}>{formatNumber(c.failed)}</TableCell>
              <TableCell className={styles.num}>{formatNumber(c.inputTokens)} / {formatNumber(c.outputTokens)}</TableCell>
              <TableCell className={styles.num}>{formatNumber(c.httpAttempts)}</TableCell>
            </TableRow>;
          })}
        </TableBody>
      </Table>
    </div>}
  </Card>;
}

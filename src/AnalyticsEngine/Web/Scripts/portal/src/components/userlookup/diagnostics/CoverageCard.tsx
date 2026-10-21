import { useEffect, useRef, useState } from 'react';
import {
  Badge,
  Button,
  Card,
  CardHeader,
  MessageBar,
  MessageBarBody,
  Subtitle2,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { fetchUserCoverage } from '../../../api/userLookupApi';
import type { CoverageCategory, UserCoverage } from '../../../types/userDiagnostics';
import Spinner from '../../Spinner';
import { useT, type TFunction, type TranslationKey } from '../../../i18n';
import { formatUtc, formatUtcDate } from './diagnosticsText';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  tableWrap: { overflowX: 'auto' },
  cellStack: { display: 'flex', flexDirection: 'column', gap: '2px' },
});

const SOURCE_STATE_KEYS: Record<string, TranslationKey> = {
  notRecorded: 'userDiagnostics.coverage.source.notRecorded',
  noStampForSource: 'userDiagnostics.coverage.source.noStampForSource',
  storageNotConfigured: 'userDiagnostics.coverage.source.storageNotConfigured',
  storageUnavailable: 'userDiagnostics.coverage.source.storageUnavailable',
};

const REPLAY_KEYS: Record<CoverageCategory['perUserReplay'], TranslationKey> = {
  perUserPossible: 'userDiagnostics.coverage.replay.perUserPossible',
  sourceWideOnly: 'userDiagnostics.coverage.replay.sourceWideOnly',
  notReconstructable: 'userDiagnostics.coverage.replay.notReconstructable',
};

/** User lookup already translates every category label; the one coverage-only category has its own key. */
function categoryLabel(t: TFunction, key: string): string {
  if (key === 'copilot-usage-report') return t('userDiagnostics.coverage.category.copilot-usage-report');
  const catalogKey = `admin.userLookup.category.${key}.label` as TranslationKey;
  const label = t(catalogKey);
  return label === catalogKey ? key : label;
}

function newestText(t: TFunction, category: CoverageCategory): { value: string; kind: string } {
  if (category.error) return { value: t('userDiagnostics.coverage.readFailed'), kind: '' };
  if (!category.latestUtc) {
    return {
      value: category.workloadsEnabled ? t('userDiagnostics.coverage.none') : t('userDiagnostics.coverage.importOff'),
      kind: '',
    };
  }
  if (category.latestKind === 'reportDate') {
    return {
      value: formatUtcDate(category.latestUtc),
      kind: category.lastActivityDateUtc
        ? t('userDiagnostics.coverage.kind.reportDate', { lastActivity: formatUtcDate(category.lastActivityDateUtc) })
        : t('userDiagnostics.coverage.kind.reportDateNoActivity'),
    };
  }
  return {
    value: formatUtc(category.latestUtc),
    kind: category.latestKind === 'observedDate' ? t('userDiagnostics.coverage.kind.observedDate') : t('userDiagnostics.coverage.kind.eventTime'),
  };
}

function sourceText(t: TFunction, category: CoverageCategory): string {
  if (!category.workloadsEnabled) return t('userDiagnostics.coverage.importOff');
  if (category.sourceStampState === 'recorded') return t('userDiagnostics.coverage.source.recorded', { when: formatUtc(category.sourceLastSuccessUtc) });
  return t(SOURCE_STATE_KEYS[category.sourceStampState ?? ''] ?? 'userDiagnostics.coverage.source.notRecorded');
}

/**
 * For each kind of data held about the person: the newest stored record (labelled with what that date measures),
 * what is known about the feeding import tenant-wide, and whether that source could ever be re-read for one person.
 * Loaded on request because it reads one row from each of about thirty tables.
 */
export default function CoverageCard({ upn }: { upn: string }) {
  const styles = useStyles();
  const t = useT();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [coverage, setCoverage] = useState<UserCoverage | null>(null);
  const controller = useRef<AbortController | null>(null);

  useEffect(() => {
    setCoverage(null);
    setError(null);
    return () => controller.current?.abort();
  }, [upn]);

  const load = async () => {
    controller.current?.abort();
    const abort = new AbortController();
    controller.current = abort;
    setLoading(true);
    setError(null);
    try {
      const result = await fetchUserCoverage(upn, abort.signal);
      if (!abort.signal.aborted) setCoverage(result);
    } catch (err) {
      if (!abort.signal.aborted) setError(err instanceof Error ? err.message : t('userDiagnostics.loadFailed'));
    } finally {
      if (!abort.signal.aborted) setLoading(false);
    }
  };

  return (
    <Card>
      <CardHeader header={<Subtitle2>{t('userDiagnostics.coverage.title')}</Subtitle2>} />
      <div className={styles.stack}>
        <Text size={200} className={styles.muted}>
          {t('userDiagnostics.coverage.description')}
        </Text>
        <div>
          <Button onClick={load} disabled={loading}>
            {t('userDiagnostics.coverage.load')}
          </Button>
        </div>
        <div aria-live="polite">
          {loading && <Spinner size={32} label={t('userDiagnostics.coverage.loading')} />}
          {error && (
            <MessageBar intent="error">
              <MessageBarBody>{error}</MessageBarBody>
            </MessageBar>
          )}
        </div>
        {coverage && (
          <div className={styles.tableWrap}>
            <Table size="small" aria-label={t('userDiagnostics.coverage.title')}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>{t('userDiagnostics.coverage.columnData')}</TableHeaderCell>
                  <TableHeaderCell>{t('userDiagnostics.coverage.columnNewest')}</TableHeaderCell>
                  <TableHeaderCell>{t('userDiagnostics.coverage.columnSource')}</TableHeaderCell>
                  <TableHeaderCell>{t('userDiagnostics.coverage.columnReplay')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {coverage.categories.map((category) => {
                  const newest = newestText(t, category);
                  return (
                    <TableRow key={category.key}>
                      <TableCell>{categoryLabel(t, category.key)}</TableCell>
                      <TableCell>
                        <div className={styles.cellStack}>
                          {category.error ? (
                            <Badge appearance="tint" color="danger">
                              {newest.value}
                            </Badge>
                          ) : (
                            <Text>{newest.value}</Text>
                          )}
                          {newest.kind && (
                            <Text size={200} className={styles.muted}>
                              {newest.kind}
                            </Text>
                          )}
                        </div>
                      </TableCell>
                      <TableCell>
                        <Text size={200}>{sourceText(t, category)}</Text>
                      </TableCell>
                      <TableCell>
                        <Text size={200}>{t(REPLAY_KEYS[category.perUserReplay] ?? 'userDiagnostics.coverage.replay.sourceWideOnly')}</Text>
                      </TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          </div>
        )}
      </div>
    </Card>
  );
}

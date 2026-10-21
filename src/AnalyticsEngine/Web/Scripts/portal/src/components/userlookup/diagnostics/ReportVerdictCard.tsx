import { useEffect, useRef, useState } from 'react';
import {
  Badge,
  Button,
  Card,
  CardHeader,
  Dropdown,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Option,
  Subtitle2,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { Copy16Regular } from '@fluentui/react-icons';
import { fetchReportVerdict } from '../../../api/userLookupApi';
import type { ReportLicenceVerdict, UserDiagnosticsContext, VerdictReport } from '../../../types/userDiagnostics';
import Spinner from '../../Spinner';
import toast from '../../toast';
import { useT, type TFunction, type TranslationKey } from '../../../i18n';
import { classificationReason, formatUtc, formatUtcDate } from './diagnosticsText';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  form: { display: 'flex', gap: '12px', flexWrap: 'wrap', alignItems: 'flex-end' },
  muted: { color: tokens.colorNeutralForeground3 },
  list: { margin: 0, paddingInlineStart: '20px', display: 'flex', flexDirection: 'column', gap: '4px' },
  mono: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 },
  verdict: { display: 'flex', alignItems: 'center', gap: '12px', flexWrap: 'wrap' },
  sql: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    margin: 0,
    padding: '8px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
});

const ROLLING_WINDOWS = [7, 28, 90, 180];

const NOTE_KEYS: Record<string, TranslationKey> = {
  noSeatLicenceTypes: 'userDiagnostics.verdict.note.noSeatLicenceTypes',
  historyPartialForPeriod: 'userDiagnostics.verdict.note.historyPartialForPeriod',
  historyUnavailableForPeriod: 'userDiagnostics.verdict.note.historyUnavailableForPeriod',
  rollingWindowUsesCurrentAssignments: 'userDiagnostics.verdict.note.rollingWindowUsesCurrentAssignments',
  licenceActivityUsesCurrentAssignments: 'userDiagnostics.verdict.note.licenceActivityUsesCurrentAssignments',
  guestExcludedFromUnlicensedLists: 'userDiagnostics.verdict.note.guestExcludedFromUnlicensedLists',
  noMatchingLicenceTypes: 'userDiagnostics.verdict.note.noMatchingLicenceTypes',
};

const REPORT_KEYS: Record<VerdictReport, TranslationKey> = {
  copilotAdoption: 'userDiagnostics.verdict.report.copilotAdoption',
  licenceActivity: 'userDiagnostics.verdict.report.licenceActivity',
};

function holdingText(t: TFunction, row: ReportLicenceVerdict['decidingRows'][number]): string {
  if (!row.validFromUtc) return t('userDiagnostics.verdict.current');
  const from = row.heldBeforeTrackingBegan ? t('userDiagnostics.history.heldBeforeTracking') : formatUtc(row.validFromUtc);
  return row.validToUtc
    ? t('userDiagnostics.verdict.held', { from, to: formatUtc(row.validToUtc) })
    : t('userDiagnostics.verdict.heldOpen', { from });
}

/**
 * "Why does this report count this person as licensed (or not)?" Re-runs the report's own licence rule for one
 * person, server-side, and shows the deciding rows. Opened from a report, it starts with that report's period and
 * seat selection.
 */
export default function ReportVerdictCard({ upn, context }: { upn: string; context?: UserDiagnosticsContext | null }) {
  const styles = useStyles();
  const t = useT();
  const fromReport = context && context.upn.toLowerCase() === upn.toLowerCase() ? context : null;
  const [report, setReport] = useState<VerdictReport>(fromReport?.report ?? 'copilotAdoption');
  const [windowDays, setWindowDays] = useState<number>(fromReport?.windowDays ?? 28);
  const [from, setFrom] = useState(fromReport?.from ?? '');
  const [to, setTo] = useState(fromReport?.to ?? '');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [verdict, setVerdict] = useState<ReportLicenceVerdict | null>(null);
  const [showSql, setShowSql] = useState(false);
  const controller = useRef<AbortController | null>(null);

  const explain = async () => {
    controller.current?.abort();
    const abort = new AbortController();
    controller.current = abort;
    setLoading(true);
    setError(null);
    setVerdict(null);
    try {
      const result = await fetchReportVerdict(
        upn,
        {
          report,
          windowDays,
          from: from || undefined,
          to: to || undefined,
          licenceTypeIds: fromReport?.licenceTypeIds,
        },
        abort.signal,
      );
      if (!abort.signal.aborted) setVerdict(result);
    } catch (err) {
      if (!abort.signal.aborted) setError(err instanceof Error ? err.message : t('userDiagnostics.loadFailed'));
    } finally {
      if (!abort.signal.aborted) setLoading(false);
    }
  };

  // Arriving from a report: answer the question that was asked without another click.
  useEffect(() => {
    setVerdict(null);
    setError(null);
    if (fromReport) void explain();
    return () => controller.current?.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [upn]);

  const copySql = async () => {
    if (!verdict?.sql) return;
    try {
      await navigator.clipboard.writeText(verdict.sql);
      toast.success(t('userDiagnostics.verdict.sqlCopied'));
    } catch {
      /* clipboard refused; the query is on screen to select by hand */
    }
  };

  return (
    <Card>
      <CardHeader header={<Subtitle2>{t('userDiagnostics.verdict.title')}</Subtitle2>} />
      <div className={styles.stack}>
        <Text size={200} className={styles.muted}>
          {t('userDiagnostics.verdict.description')}
        </Text>
        {fromReport && (
          <Text size={200} className={styles.muted}>
            {t('userDiagnostics.verdict.fromReport')}
          </Text>
        )}
        <div className={styles.form}>
          <Field label={t('userDiagnostics.verdict.report')}>
            <Dropdown
              value={t(REPORT_KEYS[report])}
              selectedOptions={[report]}
              onOptionSelect={(_e, data) => setReport((data.optionValue as VerdictReport) ?? 'copilotAdoption')}
            >
              {(Object.keys(REPORT_KEYS) as VerdictReport[]).map((key) => (
                <Option key={key} value={key}>
                  {t(REPORT_KEYS[key])}
                </Option>
              ))}
            </Dropdown>
          </Field>
          {report === 'copilotAdoption' && (
            <>
              <Field label={t('userDiagnostics.verdict.period')}>
                <Dropdown
                  value={t('userDiagnostics.verdict.rollingDays', { days: String(windowDays) })}
                  selectedOptions={[String(windowDays)]}
                  disabled={Boolean(from && to)}
                  onOptionSelect={(_e, data) => setWindowDays(Number(data.optionValue ?? 28))}
                >
                  {ROLLING_WINDOWS.map((days) => (
                    <Option key={days} value={String(days)} text={t('userDiagnostics.verdict.rollingDays', { days: String(days) })}>
                      {t('userDiagnostics.verdict.rollingDays', { days: String(days) })}
                    </Option>
                  ))}
                </Dropdown>
              </Field>
              <Field label={t('licenceActivity.dateRange.from')}>
                <Input type="date" value={from} onChange={(_e, data) => setFrom(data.value)} />
              </Field>
              <Field label={t('licenceActivity.dateRange.to')}>
                <Input type="date" value={to} onChange={(_e, data) => setTo(data.value)} />
              </Field>
            </>
          )}
          <Button appearance="primary" onClick={explain} disabled={loading}>
            {t('userDiagnostics.verdict.button')}
          </Button>
        </div>

        <div aria-live="polite">
          {loading && <Spinner size={32} label={t('userDiagnostics.verdict.loading')} />}
          {error && (
            <MessageBar intent="error">
              <MessageBarBody>{error}</MessageBarBody>
            </MessageBar>
          )}
        </div>

        {verdict && (
          <div className={styles.stack}>
            <div className={styles.verdict}>
              <Badge size="large" appearance="filled" color={verdict.verdict === 'licensed' ? 'success' : 'warning'}>
                {verdict.verdict === 'licensed' ? t('userDiagnostics.verdict.licensed') : t('userDiagnostics.verdict.unlicensed')}
              </Badge>
              <Text>
                {verdict.basis === 'licenceHistory'
                  ? t('userDiagnostics.verdict.basis.licenceHistory')
                  : t('userDiagnostics.verdict.basis.currentAssignments')}
              </Text>
            </div>
            {verdict.usesExplicitDates && verdict.fromUtc && verdict.toExclusiveUtc && (
              <Text size={200}>
                {t('userDiagnostics.verdict.customRange', {
                  from: formatUtcDate(verdict.fromUtc),
                  to: formatUtcDate(new Date(new Date(verdict.toExclusiveUtc).getTime() - 86_400_000).toISOString()),
                })}
              </Text>
            )}
            {verdict.notes.map((note) =>
              NOTE_KEYS[note] ? (
                <MessageBar key={note} intent="info">
                  <MessageBarBody>{t(NOTE_KEYS[note])}</MessageBarBody>
                </MessageBar>
              ) : null,
            )}

            <Text weight="semibold">{t('userDiagnostics.verdict.deciding')}</Text>
            {verdict.decidingRows.length === 0 ? (
              <Text>{t('userDiagnostics.verdict.noDeciding')}</Text>
            ) : (
              <ul className={styles.list}>
                {verdict.decidingRows.map((row) => (
                  <li key={`${row.licenceTypeId}-${row.validFromUtc ?? 'current'}`}>
                    <Text>
                      {row.name} <span className={styles.mono}>{row.skuPartNumber}</span>
                      {' \u2014 '}
                      {holdingText(t, row)}
                    </Text>
                  </li>
                ))}
              </ul>
            )}

            <Text weight="semibold">{t('userDiagnostics.verdict.counted')}</Text>
            <Text size={200} className={styles.muted}>
              {verdict.seatSelection === 'reportOverride'
                ? t('userDiagnostics.verdict.seatSelection.reportOverride')
                : t('userDiagnostics.verdict.seatSelection.automatic')}
            </Text>
            {verdict.countedLicenceTypes.length === 0 ? (
              <Text>{t('userDiagnostics.verdict.noneCounted')}</Text>
            ) : (
              <ul className={styles.list}>
                {verdict.countedLicenceTypes.map((licence) => (
                  <li key={licence.id}>
                    <Text>
                      {licence.name} <span className={styles.mono}>{licence.skuPartNumber}</span>
                    </Text>
                    {verdict.report === 'copilotAdoption' && (
                      <Text block size={200} className={styles.muted}>
                        {classificationReason(t, licence.classification)}
                      </Text>
                    )}
                  </li>
                ))}
              </ul>
            )}

            <Text size={200} className={styles.muted}>
              {t('userDiagnostics.verdict.recomputed', { when: formatUtc(verdict.recomputedAtUtc) })}
            </Text>
            {verdict.sql && (
              <div className={styles.stack}>
                <div className={styles.form}>
                  <Button appearance="subtle" onClick={() => setShowSql((open) => !open)} aria-expanded={showSql}>
                    {t('userDiagnostics.verdict.showSql')}
                  </Button>
                  <Button appearance="subtle" icon={<Copy16Regular />} onClick={copySql}>
                    {t('userDiagnostics.verdict.copySql')}
                  </Button>
                </div>
                {showSql && <pre className={styles.sql}>{verdict.sql}</pre>}
              </div>
            )}
          </div>
        )}
      </div>
    </Card>
  );
}

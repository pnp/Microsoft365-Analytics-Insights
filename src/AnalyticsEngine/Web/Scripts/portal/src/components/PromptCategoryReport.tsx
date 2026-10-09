import { useCallback, useEffect, useState } from 'react';
import { Button, Card, Field, MessageBar, MessageBarActions, MessageBarBody, MessageBarTitle, Select, Subtitle2, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowClockwise16Regular } from '@fluentui/react-icons';
import { fetchPromptCategoryReport, type PromptCategoryReport as Report } from '../api/promptCategoriesApi';
import { EN_CATALOG, formatDateParts, formatNumber, useT, type TranslationKey } from '../i18n';
import CategoryBarChart from './charts/CategoryBarChart';
import TimeSeriesChart from './charts/TimeSeriesChart';
import Spinner from './Spinner';
import { promptCategoryErrorText } from './promptCategoryErrors';

const useStyles = makeStyles({
  card: { display: 'flex', flexDirection: 'column', gap: '12px' },
  notes: { margin: 0, paddingInlineStart: '18px', display: 'flex', flexDirection: 'column', gap: '4px' },
  version: { maxWidth: '420px' },
  section: { display: 'flex', flexDirection: 'column', gap: '8px' },
  list: {
    listStyleType: 'none', margin: 0, padding: 0, display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))', gap: '6px 16px',
  },
  empty: { padding: '24px', textAlign: 'center', color: tokens.colorNeutralForeground3 },
  muted: { color: tokens.colorNeutralForeground3 },
});

export default function PromptCategoryReport({ months }: { months: number }) {
  const t = useT();
  const styles = useStyles();
  const [report, setReport] = useState<Report | null>(null);
  const [version, setVersion] = useState<string>();
  const [error, setError] = useState<unknown>(null);
  const [attempt, setAttempt] = useState(0);
  const retry = useCallback(() => setAttempt(a => a + 1), []);
  useEffect(() => {
    let live = true;
    setError(null);
    setReport(null);
    fetchPromptCategoryReport(months, version).then(value => { if (live) setReport(value); })
      .catch(e => { if (live) setError(e); });
    return () => { live = false; };
  }, [months, version, attempt]);
  const label = (id: string) => {
    if (id === 'other') return t('promptCategories.other');
    if (id === 'not-classified') return t('promptCategories.notClassified');
    const category = report?.categories.find(c => c.id === id);
    return category?.nameKey && category.nameKey in EN_CATALOG ? t(category.nameKey as TranslationKey) : category?.name ?? id;
  };
  const observedWeeks = report ? [...new Set(report.trend.map(row => row.weekStart))].sort() : [];
  const weeks: string[] = [];
  if (observedWeeks.length) {
    const week = new Date(`${observedWeeks[0].slice(0, 10)}T00:00:00Z`);
    const last = new Date(`${observedWeeks[observedWeeks.length - 1].slice(0, 10)}T00:00:00Z`);
    while (week <= last) {
      // Match timestamps by their UTC calendar date while preserving suppressed weeks as gaps.
      weeks.push(week.toISOString().slice(0, 10));
      week.setUTCDate(week.getUTCDate() + 7);
    }
  }
  const series = report ? report.mix.map(row => ({
    name: label(row.categoryId),
    points: weeks.map(week => ({
      weekStart: week,
      value: report.trend.find(point => point.categoryId === row.categoryId && point.weekStart.slice(0, 10) === week)?.prompts ?? null,
    })),
  })) : [];
  const hasGaps = series.some(s => s.points.some(p => p.value === null));

  return <Card className={styles.card}>
    <Subtitle2>{t('promptCategories.title')}</Subtitle2>
    <MessageBar intent="info" layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>{t('promptCategories.reportTitle')}</MessageBarTitle>
        <ul className={styles.notes}>
          <li>{t('promptCategories.sample')}</li>
          <li>{t('promptCategories.outcomes')}</li>
          <li>{t('promptCategories.accuracy')}</li>
        </ul>
      </MessageBarBody>
    </MessageBar>
    {error !== null && <MessageBar intent="error" layout="multiline">
      <MessageBarBody>{promptCategoryErrorText(error, t)}</MessageBarBody>
      <MessageBarActions><Button size="small" icon={<ArrowClockwise16Regular />} onClick={retry}>{t('promptCategories.retry')}</Button></MessageBarActions>
    </MessageBar>}
    {report === null && error === null && <Spinner label={t('promptCategories.loading')} />}
    {report && <>
      <Field className={styles.version} label={t('promptCategories.version')}>
        <Select value={report.version ?? ''} onChange={(_, data) => setVersion(data.value)}>
          {report.version && !report.versions.includes(report.version) &&
            <option value={report.version}>{report.version} · {t('promptCategories.versionUnavailable')}</option>}
          {report.versions.map(v => <option key={v} value={v}>{v}</option>)}
        </Select>
      </Field>
      {!report.mix.length && <div className={styles.empty}><Text>{t('promptCategories.empty')}</Text></div>}
      {report.mix.length > 0 && <>
        <div className={styles.section}>
          <Text weight="semibold">{t('promptCategories.mix')}</Text>
          <CategoryBarChart literalLabels categories={report.mix.map(row => ({ label: label(row.categoryId), value: row.prompts }))}
            valueLabel={t('promptCategories.prompts')} />
          <ul className={styles.list}>
            {report.mix.map(row => <li key={row.categoryId}><Text>{label(row.categoryId)}: {formatNumber(row.prompts)} {t('promptCategories.prompts')}</Text></li>)}
          </ul>
        </div>
        <div className={styles.section}>
          <Text weight="semibold">{t('promptCategories.trend')}</Text>
          <TimeSeriesChart literalLabels valueLabel={t('promptCategories.prompts')} series={series} />
          {hasGaps && <Text size={200} className={styles.muted}>{t('promptCategories.suppressedWeeks')}</Text>}
          <details>
            <summary>{t('promptCategories.weeklyValues')}</summary>
            <ul>{report.trend.map(row => <li key={`${row.weekStart}:${row.categoryId}`}><Text>
              {formatDateParts(new Date(row.weekStart), { dateStyle: 'medium', timeZone: 'UTC' })} · {label(row.categoryId)}: {formatNumber(row.prompts)}
            </Text></li>)}</ul>
          </details>
        </div>
      </>}
    </>}
  </Card>;
}

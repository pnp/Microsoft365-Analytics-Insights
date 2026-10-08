import { useEffect, useState } from 'react';
import { Card, Field, Select, Text } from '@fluentui/react-components';
import { fetchPromptCategoryReport, type PromptCategoryReport as Report } from '../api/promptCategoriesApi';
import { EN_CATALOG, formatDateParts, formatNumber, useT, type TranslationKey } from '../i18n';
import CategoryBarChart from './charts/CategoryBarChart';
import TimeSeriesChart from './charts/TimeSeriesChart';

export default function PromptCategoryReport({ months }: { months: number }) {
  const t = useT();
  const [report, setReport] = useState<Report | null>(null);
  const [version, setVersion] = useState<string>();
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let live = true;
    setFailed(false);
    setReport(null);
    fetchPromptCategoryReport(months, version).then(value => { if (live) setReport(value); })
      .catch(() => { if (live) setFailed(true); });
    return () => { live = false; };
  }, [months, version]);
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
  return <Card>
    <Text weight="semibold">{t('promptCategories.title')}</Text>
    <Text>{t('promptCategories.sample')}</Text>
    <Text>{t('promptCategories.outcomes')}</Text>
    <Text>{t('promptCategories.accuracy')}</Text>
    {failed && <Text>{t('promptCategories.error')}</Text>}
    {report && <>
      <Field label={t('promptCategories.version')}>
        <Select value={report.version ?? ''} onChange={(_, data) => setVersion(data.value)}>
          {report.version && !report.versions.includes(report.version) &&
            <option value={report.version}>{report.version} · {t('promptCategories.versionUnavailable')}</option>}
          {report.versions.map(v => <option key={v} value={v}>{v}</option>)}
        </Select>
      </Field>
      {!report.mix.length && <Text>{t('promptCategories.empty')}</Text>}
      <Text weight="semibold">{t('promptCategories.mix')}</Text>
      <CategoryBarChart categories={report.mix.map(row => ({ label: label(row.categoryId), value: row.prompts }))}
        valueLabel={t('promptCategories.prompts')} />
      {report.mix.map(row => <Text key={row.categoryId}>{label(row.categoryId)}: {formatNumber(row.prompts)} {t('promptCategories.prompts')}</Text>)}
      <Text weight="semibold">{t('promptCategories.trend')}</Text>
      <TimeSeriesChart valueLabel={t('promptCategories.prompts')}
        series={report.mix.map(row => ({
          name: label(row.categoryId),
          points: weeks.map(week => ({
            weekStart: week,
            value: report.trend.find(point => point.categoryId === row.categoryId && point.weekStart.slice(0, 10) === week)?.prompts ?? null,
          })),
        }))} />
      <details>
        <summary>{t('promptCategories.weeklyValues')}</summary>
        <ul>{report.trend.map(row => <li key={`${row.weekStart}:${row.categoryId}`}><Text>
          {formatDateParts(new Date(row.weekStart), { dateStyle: 'medium', timeZone: 'UTC' })} · {label(row.categoryId)}: {formatNumber(row.prompts)}
        </Text></li>)}</ul>
      </details>
    </>}
  </Card>;
}

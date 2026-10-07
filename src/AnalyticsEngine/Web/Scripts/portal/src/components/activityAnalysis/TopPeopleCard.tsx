import { useEffect, useId, useMemo, useState } from 'react';
import { Button, Card, Select, Spinner, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { Trophy20Regular } from '@fluentui/react-icons';
import { formatNumber, useT } from '../../i18n';
import { fetchActivityAnalysisPeople } from '../../api/activityAnalysisApi';
import type { ActivityAnalysisMetric, ActivityAnalysisPeople, ActivityAnalysisQuery } from '../../types/activityAnalysis';
import { formatMetricValue, metricLabel, metricLabelWithUnit } from './metrics';
import { useActivityTableStyles } from './tableStyles';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '14px 16px',
    minWidth: 0,
  },
  head: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
  },
  title: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    color: tokens.colorBrandForeground1,
  },
  controls: {
    display: 'flex',
    alignItems: 'flex-end',
    gap: '12px',
    flexWrap: 'wrap',
  },
  field: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  fieldLabel: {
    color: tokens.colorNeutralForeground3,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  loading: {
    opacity: 0.55,
    transitionProperty: 'opacity',
    transitionDuration: '150ms',
  },
  rank: {
    color: tokens.colorNeutralForeground3,
    minWidth: '22px',
    fontVariantNumeric: 'tabular-nums',
    flexShrink: 0,
  },
  status: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
});

const TOP_OPTIONS = [10, 25, 50, 100] as const;

/**
 * The most active matching people - the champions - ranked by one of the selected metrics. See PII
 * only: the page renders the shared "hidden" note in its place for anyone else, so this never calls
 * the people endpoint for a reader the server would refuse.
 */
export default function TopPeopleCard({
  query,
  metrics,
  matchingPeople,
}: {
  /** The query the report on screen answered. */
  query: ActivityAnalysisQuery;
  metrics: ReadonlyMap<string, ActivityAnalysisMetric>;
  matchingPeople: number;
}) {
  const styles = useStyles();
  const table = useActivityTableStyles();
  const t = useT();
  const rankId = useId();
  const topId = useId();
  const [rankBy, setRankBy] = useState<string>(query.metrics[0]);
  const [top, setTop] = useState<number>(TOP_OPTIONS[0]);
  const [attempt, setAttempt] = useState(0);
  const [state, setState] = useState<{ key: string; data?: ActivityAnalysisPeople; error?: string } | null>(null);

  // Ranked by the first selected metric unless the reader picks another; a metric since deselected
  // falls back to the first again.
  const sort = query.metrics.includes(rankBy) ? rankBy : query.metrics[0];
  const peopleQuery = useMemo(() => ({ ...query, sort, top }), [query, sort, top]);
  const key = JSON.stringify(peopleQuery);

  useEffect(() => {
    if (matchingPeople === 0) return;
    const controller = new AbortController();
    fetchActivityAnalysisPeople(peopleQuery, controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) setState({ key, data });
      })
      .catch((e: unknown) => {
        if (controller.signal.aborted) return;
        setState({ key, error: e instanceof Error ? e.message : String(e) });
      });
    return () => controller.abort();
  }, [key, attempt, matchingPeople]);

  const loading = matchingPeople > 0 && state?.key !== key;
  const data = state?.data ?? null;
  const error = state?.key === key ? state.error : undefined;
  const sortLabel = metricLabel(t, sort, metrics.get(sort)?.label);

  return (
    <Card className={styles.card}>
      <div className={styles.head}>
        <div>
          <div className={styles.title}>
            <Trophy20Regular aria-hidden="true" />
            <Text as="h2" weight="semibold" size={500} style={{ margin: 0, color: tokens.colorNeutralForeground1 }}>
              {t('activityAnalysis.topPeople.title')}
            </Text>
          </div>
          <Text size={200} className={styles.muted}>
            {t('activityAnalysis.topPeople.subtitle')}
          </Text>
        </div>
        <div className={styles.controls} data-print="hide">
          <div className={styles.field}>
            <Text id={rankId} size={200} className={styles.fieldLabel}>
              {t('activityAnalysis.topPeople.rankBy')}
            </Text>
            <Select size="small" aria-labelledby={rankId} value={sort} onChange={(_e, d) => setRankBy(d.value)}>
              {query.metrics.map((metricKey) => (
                <option key={metricKey} value={metricKey}>
                  {metricLabel(t, metricKey, metrics.get(metricKey)?.label)}
                </option>
              ))}
            </Select>
          </div>
          <div className={styles.field}>
            <Text id={topId} size={200} className={styles.fieldLabel}>
              {t('activityAnalysis.topPeople.show')}
            </Text>
            <Select size="small" aria-labelledby={topId} value={String(top)} onChange={(_e, d) => setTop(Number(d.value))}>
              {TOP_OPTIONS.map((n) => (
                <option key={n} value={n}>
                  {t('activityAnalysis.topPeople.topN', { count: formatNumber(n) })}
                </option>
              ))}
            </Select>
          </div>
        </div>
      </div>

      {matchingPeople === 0 ? (
        <Text size={200} className={styles.muted}>
          {t('activityAnalysis.matrix.empty')}
        </Text>
      ) : error ? (
        <div className={styles.status} role="alert">
          <Text size={200}>{error}</Text>
          <Button size="small" onClick={() => setAttempt((a) => a + 1)}>
            {t('activityAnalysis.page.retry')}
          </Button>
        </div>
      ) : !data ? (
        <div className={styles.status}>
          <Spinner size="tiny" />
          <Text size={200}>{t('activityAnalysis.topPeople.loading')}</Text>
        </div>
      ) : data.people.length === 0 ? (
        <Text size={200} className={styles.muted}>
          {t('activityAnalysis.topPeople.empty', { metric: sortLabel })}
        </Text>
      ) : (
        <>
          <div className={mergeClasses(table.scroll, loading ? styles.loading : undefined)} role="region" aria-label={t('activityAnalysis.topPeople.title')} tabIndex={0} aria-busy={loading}>
            <table className={table.table}>
              <thead>
                <tr>
                  <th scope="col" className={mergeClasses(table.th, table.corner)}>
                    {t('activityAnalysis.topPeople.person')}
                  </th>
                  <th scope="col" className={mergeClasses(table.th, table.thLeft)}>
                    {t('activityAnalysis.matrix.department')}
                  </th>
                  {query.metrics.map((metricKey) => {
                    const label = metricLabelWithUnit(t, metricKey, metrics.get(metricKey));
                    return (
                      <th
                        key={metricKey}
                        scope="col"
                        className={mergeClasses(table.th, table.thMetric, metricKey === sort ? table.highlight : undefined)}
                      >
                        <span className={table.metricName} title={label}>
                          {label}
                        </span>
                      </th>
                    );
                  })}
                </tr>
              </thead>
              <tbody>
                {data.people.map((person, index) => {
                  const values = new Map(person.values.map((v) => [v.metric, v.sum]));
                  return (
                    <tr key={person.userPrincipalName} className={table.row}>
                      <th scope="row" className={mergeClasses(table.td, table.first)}>
                        <span className={table.nameCell}>
                          <span className={styles.rank}>{formatNumber(index + 1)}</span>
                          <span className={table.name} title={person.userPrincipalName}>
                            {person.userPrincipalName}
                          </span>
                        </span>
                      </th>
                      <td className={mergeClasses(table.td, table.thLeft)}>
                        {person.department ?? t('activityAnalysis.group.noDepartment')}
                      </td>
                      {query.metrics.map((metricKey) => {
                        const value = values.get(metricKey);
                        return (
                          <td key={metricKey} className={mergeClasses(table.td, metricKey === sort ? table.highlight : undefined)}>
                            {value == null ? '\u2013' : formatMetricValue(metrics.get(metricKey), value)}
                          </td>
                        );
                      })}
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          <Text size={200} className={styles.muted}>
            {t('activityAnalysis.topPeople.showing', {
              shown: formatNumber(data.people.length),
              total: formatNumber(data.totalPeople),
            })}
          </Text>
        </>
      )}
    </Card>
  );
}

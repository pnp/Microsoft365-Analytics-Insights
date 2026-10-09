import { useMemo, useState } from 'react';
import { Button, Card, Text, makeStyles } from '@fluentui/react-components';
import { useT } from '../../i18n';
import type { ActivityAnalysisMetric, ActivityAnalysisReport } from '../../types/activityAnalysis';
import type { ReportSeries } from '../../types/reports';
import TimeSeriesChart from '../charts/TimeSeriesChart';
import InfoTip from '../shared/InfoTip';
import { displayValue, metricLabel, metricLabelWithUnit } from './metrics';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    padding: '14px 16px',
    minWidth: 0,
  },
  head: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '8px',
    flexWrap: 'wrap',
  },
  title: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
  },
  modes: {
    display: 'flex',
    gap: '4px',
  },
});

export type TimelineMode = 'sum' | 'activePeople';

/** Rounds an hours figure for the chart: a tenth of an hour is as fine as a weekly line needs. */
function roundForChart(value: number): number {
  return Math.round(value * 10) / 10;
}

/**
 * The selected metrics week by week: total activity (durations in hours), or the people with any of it.
 * One line per metric, so the colours go round more than once when many are selected - the chart then
 * switches to dashed and dotted lines rather than repeating a colour.
 */
export function timelineSeries(
  report: Pick<ActivityAnalysisReport, 'series' | 'weekStarts'>,
  metrics: ReadonlyMap<string, ActivityAnalysisMetric>,
  mode: TimelineMode,
  labelFor: (key: string, metric: ActivityAnalysisMetric | undefined, mode: TimelineMode) => string,
): ReportSeries[] {
  return report.series.map((series) => {
    const metric = metrics.get(series.metric);
    const values = mode === 'sum' ? series.sum.map((v) => roundForChart(displayValue(metric, v))) : series.activePeople;
    return {
      name: labelFor(series.metric, metric, mode),
      points: report.weekStarts.map((weekStart, i) => ({ weekStart, value: values[i] ?? 0 })),
    };
  });
}

export default function MetricsByWeekChart({
  report,
  metrics,
}: {
  report: ActivityAnalysisReport;
  metrics: ReadonlyMap<string, ActivityAnalysisMetric>;
}) {
  const styles = useStyles();
  const t = useT();
  const [mode, setMode] = useState<TimelineMode>('sum');

  const series = useMemo(
    () =>
      timelineSeries(report, metrics, mode, (key, metric, m) =>
        m === 'sum' ? metricLabelWithUnit(t, key, metric) : metricLabel(t, key, metric?.label),
      ),
    [report, metrics, mode, t],
  );

  const modeLabel = mode === 'sum' ? t('activityAnalysis.chart.timeline.mode.sum') : t('activityAnalysis.chart.timeline.mode.activePeople');

  return (
    <Card className={styles.card}>
      <div className={styles.head}>
        <div className={styles.title}>
          <Text as="h2" weight="semibold" size={400} style={{ margin: 0 }}>
            {t('activityAnalysis.chart.timeline.title')}
          </Text>
          <InfoTip
            title={t('activityAnalysis.chart.timeline.title')}
            content={{
              what: t('activityAnalysis.chart.timeline.info.what'),
              how: t('activityAnalysis.chart.timeline.info.how'),
            }}
          />
        </div>
        <div className={styles.modes} role="group" aria-label={t('activityAnalysis.chart.timeline.modeAria')} data-print="hide">
          <Button
            size="small"
            appearance={mode === 'sum' ? 'primary' : 'secondary'}
            aria-pressed={mode === 'sum'}
            onClick={() => setMode('sum')}
          >
            {t('activityAnalysis.chart.timeline.mode.sum')}
          </Button>
          <Button
            size="small"
            appearance={mode === 'activePeople' ? 'primary' : 'secondary'}
            aria-pressed={mode === 'activePeople'}
            onClick={() => setMode('activePeople')}
          >
            {t('activityAnalysis.chart.timeline.mode.activePeople')}
          </Button>
        </div>
      </div>
      <TimeSeriesChart series={series} valueLabel={series.length === 1 ? series[0].name : modeLabel} height={320} />
    </Card>
  );
}

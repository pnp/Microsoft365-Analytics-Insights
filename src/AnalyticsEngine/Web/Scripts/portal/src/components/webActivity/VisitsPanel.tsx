import CategoryBarChart from '../charts/CategoryBarChart';
import DonutChart from '../charts/DonutChart';
import StackedAreaChart from '../charts/StackedAreaChart';
import TimeSeriesChart from '../charts/TimeSeriesChart';
import { KpiGrid, type KpiDefinition } from '../shared/KpiGrid';
import { seriesColor } from '../charts/chartCommon';
import type { WebActivityVisits } from '../../types/webActivity';
import { useT } from '../../i18n';
import {
  FailedQueryNote,
  SectionCard,
  WindowNote,
  bucketsToCategories,
  translatedBucketsToCategories,
  formatCount,
  formatDecimal,
  formatHour,
  formatPct,
  queryFor,
  toCategories,
  toStackedSeries,
  useWebActivityStyles,
  withRemainder,
} from './webActivityShared';

/**
 * The Visits tab - the in-app replacement for the Power BI report's "Visits" page.
 *
 * Keeps that report's panels (by site, by page, by device, by browser, by day, by period of day,
 * popular hours, site over time) because they are the ones intranet owners actually used, and adds
 * the two it was missing: how many visits arrive outside working hours, and how many visits each
 * visitor makes.
 */
export default function VisitsPanel({ data }: { data: WebActivityVisits }) {
  const styles = useWebActivityStyles();
  const t = useT();
  const kpis = data.kpis;

  // Listed devices plus the visits they do not account for, so the ring cannot imply it covers
  // every visit when the device list was truncated.
  const deviceCategories = withRemainder(toCategories(data.byDevice), kpis.visits, t('webActivity.common.otherUnknown'));

  const items: KpiDefinition[] = [
    {
      key: 'visits',
      label: t('webActivity.visits.kpi.totalVisits'),
      value: formatCount(kpis.visits),
      info: {
        what: t('webActivity.visits.kpi.totalVisitsWhat'),
        how: t('webActivity.visits.kpi.totalVisitsHow'),
      },
    },
    {
      key: 'visitors',
      label: t('webActivity.visits.kpi.uniqueVisitors'),
      value: formatCount(kpis.visitors),
      hint: t('webActivity.visits.kpi.visitsEachHint', { count: formatDecimal(kpis.visitsPerVisitor) }),
      info: {
        what: t('webActivity.visits.kpi.uniqueVisitorsWhat'),
        how: t('webActivity.visits.kpi.uniqueVisitorsHow'),
      },
    },
    {
      key: 'pages',
      label: t('webActivity.visits.kpi.uniquePages'),
      value: formatCount(kpis.uniquePages),
      info: {
        what: t('webActivity.visits.kpi.uniquePagesWhat'),
        how: t('webActivity.visits.kpi.uniquePagesHow'),
      },
    },
    {
      key: 'earliest',
      label: t('webActivity.visits.kpi.earliestVisit'),
      value: formatHour(kpis.earliestVisitHour),
      hint: 'UTC',
      info: {
        what: t('webActivity.visits.kpi.earliestVisitWhat'),
        how: t('webActivity.visits.kpi.earliestVisitHow'),
      },
    },
    {
      key: 'latest',
      label: t('webActivity.visits.kpi.latestVisit'),
      value: formatHour(kpis.latestVisitHour),
      hint: 'UTC',
      info: {
        what: t('webActivity.visits.kpi.latestVisitWhat'),
        how: t('webActivity.visits.kpi.latestVisitHow'),
      },
    },
    {
      key: 'out-of-hours',
      label: t('webActivity.visits.kpi.outOfHours'),
      value: formatPct(kpis.outOfHoursPct),
      hint: t('webActivity.common.visitsCount', { count: formatCount(kpis.outOfHoursVisits) }),
      info: {
        what: t('webActivity.visits.kpi.outOfHoursWhat'),
        how: t('webActivity.visits.kpi.outOfHoursHow'),
      },
    },
  ];

  return (
    <div>
      <div style={{ marginTop: '12px' }}>
        <WindowNote window={data.window} />
      </div>

      <FailedQueryNote queries={data.queries} />

      <div style={{ marginTop: '12px' }}>
        <KpiGrid items={items} />
      </div>

      <div className={styles.stack}>
        <SectionCard
          title={t('webActivity.visits.trend.title')}
          description={t('webActivity.visits.trend.description')}
          query={queryFor(data.queries, 'visits-trend')}
          isEmpty={data.trend.length === 0}
        >
          <TimeSeriesChart
            valueLabel={t('webActivity.common.count')}
            series={[
              { name: t('webActivity.common.visits'), points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.visits })) },
              { name: t('webActivity.common.visitors'), points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.visitors })) },
            ]}
          />
        </SectionCard>
      </div>

      <div className={styles.grid}>
        <SectionCard
          title={t('webActivity.visits.bySite.title')}
          query={queryFor(data.queries, 'visits-sites')}
          isEmpty={data.bySite.length === 0}
        >
          <CategoryBarChart categories={toCategories(data.bySite)} valueLabel={t('webActivity.common.visits')} />
        </SectionCard>

        <SectionCard
          title={t('webActivity.visits.byPage.title')}
          description={t('webActivity.visits.byPage.description')}
          query={queryFor(data.queries, 'visits-pages')}
          isEmpty={data.byPage.length === 0}
        >
          <CategoryBarChart categories={toCategories(data.byPage)} valueLabel={t('webActivity.common.visits')} />
        </SectionCard>

        <SectionCard
          title={t('webActivity.visits.byDevice.title')}
          query={queryFor(data.queries, 'visits-devices')}
          isEmpty={data.byDevice.length === 0}
          emptyMessage={t('webActivity.visits.byDevice.empty')}
        >
          <DonutChart
            categories={deviceCategories}
            colours={deviceCategories.map((_, i) => seriesColor(i))}
            centreValue={formatCount(kpis.visits)}
            centreLabel={t('webActivity.common.visitsLower')}
          />
        </SectionCard>

        <SectionCard
          title={t('webActivity.visits.byBrowser.title')}
          query={queryFor(data.queries, 'visits-browsers')}
          isEmpty={data.byBrowser.length === 0}
        >
          <CategoryBarChart categories={toCategories(data.byBrowser)} valueLabel={t('webActivity.common.visits')} />
        </SectionCard>

        <SectionCard
          title={t('webActivity.visits.byDay.title')}
          query={queryFor(data.queries, 'visits-heatmap')}
          isEmpty={data.byDay.every((b) => b.count === 0)}
        >
          <CategoryBarChart categories={translatedBucketsToCategories(t, 'day', data.byDay)} valueLabel={t('webActivity.common.visits')} showShare />
        </SectionCard>

        <SectionCard
          title={t('webActivity.visits.byPeriod.title')}
          description={t('webActivity.visits.byPeriod.description')}
          query={queryFor(data.queries, 'visits-heatmap')}
          isEmpty={data.byPeriodOfDay.every((b) => b.count === 0)}
        >
          <CategoryBarChart categories={translatedBucketsToCategories(t, 'period', data.byPeriodOfDay)} valueLabel={t('webActivity.common.visits')} showShare />
        </SectionCard>

        <SectionCard
          title={t('webActivity.visits.popularHours.title')}
          description={t('webActivity.visits.popularHours.description')}
          query={queryFor(data.queries, 'visits-heatmap')}
          isEmpty={data.byHour.every((b) => b.count === 0)}
        >
          <CategoryBarChart
            categories={bucketsToCategories(data.byHour).filter((c) => c.value > 0)}
            valueLabel={t('webActivity.common.visits')}
          />
        </SectionCard>
      </div>

      <div className={styles.stack}>
        <SectionCard
          title={t('webActivity.visits.siteMix.title')}
          description={t('webActivity.visits.siteMix.description')}
          query={queryFor(data.queries, 'visits-site-over-time')}
          isEmpty={data.siteOverTime.length === 0}
        >
          <StackedAreaChart series={toStackedSeries(data.siteOverTime)} valueLabel={t('webActivity.common.pageViews')} />
        </SectionCard>
      </div>
    </div>
  );
}

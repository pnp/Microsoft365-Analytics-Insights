import CategoryBarChart from '../charts/CategoryBarChart';
import DonutChart from '../charts/DonutChart';
import StackedAreaChart from '../charts/StackedAreaChart';
import TimeSeriesChart from '../charts/TimeSeriesChart';
import { KpiGrid, type KpiDefinition } from '../shared/KpiGrid';
import { seriesColor } from '../charts/chartCommon';
import type { WebActivityVisits } from '../../types/webActivity';
import {
  FailedQueryNote,
  SectionCard,
  WindowNote,
  bucketsToCategories,
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
  const kpis = data.kpis;

  // Listed devices plus the visits they do not account for, so the ring cannot imply it covers
  // every visit when the device list was truncated.
  const deviceCategories = withRemainder(toCategories(data.byDevice), kpis.visits, 'Other / unknown');

  const items: KpiDefinition[] = [
    {
      key: 'visits',
      label: 'Total visits',
      value: formatCount(kpis.visits),
      info: {
        what: 'Browsing sessions with at least one page view in the period.',
        how: 'Distinct Application Insights sessions behind the page views in the window.',
      },
    },
    {
      key: 'visitors',
      label: 'Unique visitors',
      value: formatCount(kpis.visitors),
      hint: `${formatDecimal(kpis.visitsPerVisitor)} visits each`,
      info: {
        what: 'Distinct people behind those visits.',
        how: 'Distinct users on the visiting sessions. Sessions with no identified user are excluded.',
      },
    },
    {
      key: 'pages',
      label: 'Unique pages',
      value: formatCount(kpis.uniquePages),
      info: {
        what: 'Distinct pages that were viewed at least once.',
        how: 'Distinct URLs across the page views in the window.',
      },
    },
    {
      key: 'earliest',
      label: 'Earliest visit',
      value: formatHour(kpis.earliestVisitHour),
      hint: 'UTC',
      info: {
        what: 'The earliest hour of the day at which any visit started.',
        how:
          'Hour of the earliest visit start in the window, in UTC. Useful mainly as a sanity check '
          + 'on where your people actually are: an intranet with an 03:00 UTC start has a timezone '
          + 'you may not have planned maintenance around.',
      },
    },
    {
      key: 'latest',
      label: 'Latest visit',
      value: formatHour(kpis.latestVisitHour),
      hint: 'UTC',
      info: {
        what: 'The latest hour of the day at which any visit started.',
        how: 'Hour of the latest visit start in the window, in UTC.',
      },
    },
    {
      key: 'out-of-hours',
      label: 'Out of hours',
      value: formatPct(kpis.outOfHoursPct),
      hint: `${formatCount(kpis.outOfHoursVisits)} visits`,
      info: {
        what: 'Visits that started outside 07:00-19:00 UTC, or at the weekend.',
        how:
          'A fixed UTC working window, not a per-user local one - the import does not record a '
          + 'visitor timezone. On a multi-region intranet a high figure usually means offices in '
          + 'other timezones rather than people working late, so read it with the geography tab.',
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
          title="Visits and visitors over time"
          description="Weekly, with each visit counted in the week it started."
          query={queryFor(data.queries, 'visits-trend')}
          isEmpty={data.trend.length === 0}
        >
          <TimeSeriesChart
            valueLabel="Count"
            series={[
              { name: 'Visits', points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.visits })) },
              { name: 'Visitors', points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.visitors })) },
            ]}
          />
        </SectionCard>
      </div>

      <div className={styles.grid}>
        <SectionCard
          title="Visits by site"
          query={queryFor(data.queries, 'visits-sites')}
          isEmpty={data.bySite.length === 0}
        >
          <CategoryBarChart categories={toCategories(data.bySite)} valueLabel="Visits" />
        </SectionCard>

        <SectionCard
          title="Visits by page"
          description="The pages seen in the most visits, rather than the pages with the most views."
          query={queryFor(data.queries, 'visits-pages')}
          isEmpty={data.byPage.length === 0}
        >
          <CategoryBarChart categories={toCategories(data.byPage)} valueLabel="Visits" />
        </SectionCard>

        <SectionCard
          title="Visits by device"
          query={queryFor(data.queries, 'visits-devices')}
          isEmpty={data.byDevice.length === 0}
          emptyMessage="No device was recorded for any visit. Device is derived from the browser's user agent by Application Insights and is not always populated."
        >
          <DonutChart
            categories={deviceCategories}
            colours={deviceCategories.map((_, i) => seriesColor(i))}
            centreValue={formatCount(kpis.visits)}
            centreLabel="visits"
          />
        </SectionCard>

        <SectionCard
          title="Visits by browser"
          query={queryFor(data.queries, 'visits-browsers')}
          isEmpty={data.byBrowser.length === 0}
        >
          <CategoryBarChart categories={toCategories(data.byBrowser)} valueLabel="Visits" />
        </SectionCard>

        <SectionCard
          title="Visits by day of week"
          query={queryFor(data.queries, 'visits-heatmap')}
          isEmpty={data.byDay.every((b) => b.count === 0)}
        >
          <CategoryBarChart categories={bucketsToCategories(data.byDay)} valueLabel="Visits" showShare />
        </SectionCard>

        <SectionCard
          title="Visits by period of day"
          description="UTC. After midnight 00-04, early morning 05-08, late morning 09-11, afternoon 12-16, evening 17-20, late night 21-23."
          query={queryFor(data.queries, 'visits-heatmap')}
          isEmpty={data.byPeriodOfDay.every((b) => b.count === 0)}
        >
          <CategoryBarChart categories={bucketsToCategories(data.byPeriodOfDay)} valueLabel="Visits" showShare />
        </SectionCard>

        <SectionCard
          title="Popular hours"
          description="When visits start, by hour of the day (UTC)."
          query={queryFor(data.queries, 'visits-heatmap')}
          isEmpty={data.byHour.every((b) => b.count === 0)}
        >
          <CategoryBarChart
            categories={bucketsToCategories(data.byHour).filter((c) => c.value > 0)}
            valueLabel="Visits"
          />
        </SectionCard>
      </div>

      <div className={styles.stack}>
        <SectionCard
          title="Site mix over time"
          description="Weekly page views per site, stacked, so a site that is growing or dying stands out."
          query={queryFor(data.queries, 'visits-site-over-time')}
          isEmpty={data.siteOverTime.length === 0}
        >
          <StackedAreaChart series={toStackedSeries(data.siteOverTime)} valueLabel="Page views" />
        </SectionCard>
      </div>
    </div>
  );
}

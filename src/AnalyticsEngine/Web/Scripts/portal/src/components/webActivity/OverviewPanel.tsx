import CategoryBarChart from '../charts/CategoryBarChart';
import DonutChart from '../charts/DonutChart';
import HeatmapChart from '../charts/HeatmapChart';
import TimeSeriesChart from '../charts/TimeSeriesChart';
import { KpiGrid, type KpiDefinition } from '../shared/KpiGrid';
import { seriesColor } from '../charts/chartCommon';
import type { WebActivityOverview } from '../../types/webActivity';
import {
  DWELL_CAVEAT,
  FailedQueryNote,
  JudgementList,
  SectionCard,
  WindowNote,
  bounceTone,
  bucketsToCategories,
  formatCount,
  formatDecimal,
  formatDuration,
  formatPct,
  formatSeconds,
  loadTone,
  queryFor,
  reachToneOrNeutral,
  toCategories,
  useWebActivityStyles,
} from './webActivityShared';

/**
 * The Overview tab: the five numbers an intranet owner is actually asked for, plus the page's own
 * reading of them.
 *
 * The judgements sit directly under the KPIs rather than at the bottom. A figure without a verdict
 * invites the reader to supply their own, and "38% bounce rate" means nothing at all to someone who
 * has never run a web analytics tool before - which is most SharePoint administrators.
 */
export default function OverviewPanel({
  data,
  directoryImported,
}: {
  data: WebActivityOverview;
  directoryImported: boolean;
}) {
  const styles = useWebActivityStyles();
  const kpis = data.kpis;
  const reachMeasurable = kpis.reachPct !== null && kpis.directoryImported && directoryImported;

  const items: KpiDefinition[] = [
    {
      key: 'visits',
      label: 'Visits',
      value: formatCount(kpis.visits),
      hint: `${formatDecimal(kpis.pagesPerVisit)} pages per visit`,
      info: {
        what: 'Browsing sessions on the SharePoint sites the tracker is deployed to.',
        how:
          'One visit is one Application Insights session that had at least one page view in the '
          + 'window. A session that started before the window began is counted from its first page '
          + 'view inside the window, so two adjacent periods never double-count the same visit.',
      },
    },
    {
      key: 'visitors',
      label: 'Visitors',
      value: formatCount(kpis.visitors),
      hint: reachMeasurable
        ? `${formatPct(kpis.reachPct)} of ${formatCount(kpis.knownUsers)} enabled directory users`
        : 'no directory to measure against',
      tone: reachMeasurable ? reachToneOrNeutral(kpis.reachPct) : 'neutral',
      info: {
        what: 'Distinct people who visited at least once.',
        how:
          'The percentage is enabled directory users who visited, over all enabled directory users '
          + '- which includes guests, shared mailboxes and service accounts, and excludes any site '
          + 'the tracker is not deployed to, so it is a floor on real reach rather than a precise '
          + 'figure. Without the Graph user import there is no population to divide by, so no '
          + 'percentage is shown.',
      },
    },
    {
      key: 'pageviews',
      label: 'Page views',
      value: formatCount(kpis.pageViews),
      hint: `${formatCount(kpis.uniquePageViews)} unique, across ${formatCount(kpis.uniquePages)} pages`,
      info: {
        what: 'Every page view recorded, and how many distinct pages they landed on.',
        how:
          'Unique page views count a page once per visit, the same definition Google Analytics uses, '
          + 'so a reader refreshing an article inflates page views but not unique page views.',
      },
    },
    {
      key: 'bounce',
      label: 'Single-page visits',
      value: formatPct(kpis.bouncePct),
      hint: 'people who saw one page and left',
      tone: bounceTone(kpis.bouncePct),
      info: {
        what: 'The share of visits that saw exactly one page.',
        how:
          'Visits with exactly one page view, over all visits. High is not automatically bad - a '
          + 'deep link to one policy document is a legitimate one-page visit - but on a home page it '
          + 'means the landing page is not leading anywhere.',
      },
    },
    {
      key: 'load',
      label: 'Average page load',
      value: formatSeconds(kpis.averageLoadSeconds),
      hint: `${formatDuration(kpis.averageSecondsOnPage)} average time on page`,
      tone: loadTone(kpis.averageLoadSeconds),
      info: {
        what: 'How long a page took to become usable, as the browser measured it.',
        how:
          'Mean of the page load time the tracker reports per view, in seconds. Views where the '
          + 'browser did not report a load time are excluded rather than counted as zero, and a dash '
          + 'means none reported one. The mean hides the slow tail: the Technology tab carries the '
          + '95th percentile, and the Page views tab ranks the slowest pages. Average time on page ' + DWELL_CAVEAT.charAt(0).toLowerCase()
          + DWELL_CAVEAT.slice(1),
      },
    },
    {
      key: 'returning',
      label: 'Seen in both halves',
      value: formatCount(kpis.returningVisitors),
      hint: `${formatCount(kpis.newVisitors)} first seen in the second half`,
      info: {
        what: 'People who visited in both halves of the period, and people who appeared only in the second.',
        how:
          'This is new to THIS WINDOW, not new to the intranet. Someone whose first ever visit '
          + 'predates the window is indistinguishable here from someone who simply did not visit in '
          + 'the first half. Establishing a genuine first-ever visit would mean scanning the entire '
          + 'page-view history, which is exactly the query this page must never run.',
      },
    },
  ];

  const segmentCategories = bucketsToCategories(data.visitorSegments).filter((c) => c.value > 0);

  return (
    <div>
      <div style={{ marginTop: '12px' }}>
        <WindowNote window={data.window} />
      </div>

      <FailedQueryNote queries={data.queries} />

      <div style={{ marginTop: '12px' }}>
        <KpiGrid items={items} />
      </div>

      <JudgementList judgements={data.judgements} />

      <div className={styles.stack}>
        <SectionCard
          title="Traffic over time"
          description="Weekly page views, visits and visitors. Visits are counted in the week they started."
          note={
            'The first and last bars can be partial weeks, because the window rarely starts and ends '
            + 'on a Monday. A visit already open when the window began is measured from its first '
            + 'page view inside it, so it can appear as a short - even single-page - visit.'
          }
          query={queryFor(data.queries, 'overview-trend')}
          isEmpty={data.trend.length === 0}
        >
          <TimeSeriesChart
            valueLabel="Count"
            series={[
              { name: 'Page views', points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.pageViews })) },
              { name: 'Visits', points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.visits })) },
              { name: 'Visitors', points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.visitors })) },
              {
                name: 'Single-page visits',
                points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.bounces })),
              },
            ]}
          />
        </SectionCard>
      </div>

      <div className={styles.grid}>
        <SectionCard
          title="How habitually people visit"
          description="Visitors grouped by how many separate days they came back."
          note={
            data.window.segmentsFullyReachable
              ? 'Bands are a share of the days in the period, so "Daily" means the same thing over a '
                + 'month as it does over a year.'
              : 'Bands are a share of the days in the period. This period is too short to separate '
                + 'the lower bands - with only a handful of possible day counts there is nothing '
                + 'between "one-off" and "regular", so an empty band here means "cannot tell", not '
                + '"nobody".'
          }
          query={queryFor(data.queries, 'overview-segments')}
          isEmpty={segmentCategories.length === 0}
        >
          <DonutChart
            categories={segmentCategories}
            colours={segmentCategories.map((_, i) => seriesColor(i))}
            centreValue={formatCount(kpis.visitors)}
            centreLabel="visitors"
          />
        </SectionCard>

        <SectionCard
          title="How far people get"
          description="Visits by the number of pages they saw."
          query={queryFor(data.queries, 'overview-depth')}
          isEmpty={data.visitDepth.every((b) => b.count === 0)}
        >
          <CategoryBarChart
            categories={bucketsToCategories(data.visitDepth)}
            valueLabel="Visits"
            showShare
          />
        </SectionCard>

        <SectionCard
          title="Busiest sites"
          description="Page views per SharePoint site, as a share of ALL page views in the period."
          note={
            'Only the top ' + data.window.top + ' are shown, so these do not add up to 100% - the '
            + 'remainder is the tail this chart does not list.'
          }
          query={queryFor(data.queries, 'overview-sites')}
          isEmpty={data.topSites.length === 0}
          emptyMessage="No page views could be attributed to a site. The tracker reports the site URL as a custom property - if it is missing, hits are still counted but cannot be grouped."
        >
          <CategoryBarChart categories={toCategories(data.topSites)} valueLabel="Page views" />
        </SectionCard>
      </div>

      <div className={styles.stack}>
        <SectionCard
          title="When the intranet is used"
          description="Page views by day of the week and hour of the day."
          query={queryFor(data.queries, 'overview-heatmap')}
          isEmpty={data.heatmap.length === 0}
        >
          <HeatmapChart
            cells={data.heatmap.map((cell) => ({
              dayOfWeek: cell.day,
              hour: cell.hour,
              value: cell.pageViews,
            }))}
            valueLabel="page views"
            footnote="Hours are UTC. Page views are stored in UTC and a multi-region intranet has no single local clock to convert to."
          />
        </SectionCard>
      </div>
    </div>
  );
}

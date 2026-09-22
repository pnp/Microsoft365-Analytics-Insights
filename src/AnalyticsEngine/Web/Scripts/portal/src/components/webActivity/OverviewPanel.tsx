import CategoryBarChart from '../charts/CategoryBarChart';
import DonutChart from '../charts/DonutChart';
import HeatmapChart from '../charts/HeatmapChart';
import TimeSeriesChart from '../charts/TimeSeriesChart';
import { KpiGrid, type KpiDefinition } from '../shared/KpiGrid';
import { seriesColor } from '../charts/chartCommon';
import type { WebActivityOverview } from '../../types/webActivity';
import { useT } from '../../i18n';
import {
  dwellCaveatLower,
  FailedQueryNote,
  JudgementList,
  SectionCard,
  WindowNote,
  bounceTone,
  bucketsToCategories,
  translatedBucketsToCategories,
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
  const t = useT();
  const kpis = data.kpis;
  const reachMeasurable = kpis.reachPct !== null && kpis.directoryImported && directoryImported;

  const items: KpiDefinition[] = [
    {
      key: 'visits',
      label: t('webActivity.common.visits'),
      value: formatCount(kpis.visits),
      hint: t('webActivity.overview.kpi.pagesPerVisitHint', { count: formatDecimal(kpis.pagesPerVisit) }),
      info: {
        what: t('webActivity.overview.kpi.visitsWhat'),
        how: t('webActivity.overview.kpi.visitsHow'),
      },
    },
    {
      key: 'visitors',
      label: t('webActivity.common.visitors'),
      value: formatCount(kpis.visitors),
      hint: reachMeasurable
        ? t('webActivity.overview.kpi.reachHint', { pct: formatPct(kpis.reachPct), count: formatCount(kpis.knownUsers) })
        : t('webActivity.overview.kpi.noDirectoryHint'),
      tone: reachMeasurable ? reachToneOrNeutral(kpis.reachPct) : 'neutral',
      info: {
        what: t('webActivity.overview.kpi.visitorsWhat'),
        how: t('webActivity.overview.kpi.visitorsHow'),
      },
    },
    {
      key: 'pageviews',
      label: t('webActivity.common.pageViews'),
      value: formatCount(kpis.pageViews),
      hint: t('webActivity.overview.kpi.pageViewsHint', { unique: formatCount(kpis.uniquePageViews), pages: formatCount(kpis.uniquePages) }),
      info: {
        what: t('webActivity.overview.kpi.pageViewsWhat'),
        how: t('webActivity.overview.kpi.pageViewsHow'),
      },
    },
    {
      key: 'bounce',
      label: t('webActivity.common.singlePageVisits'),
      value: kpis.visits > 0 ? formatPct(kpis.bouncePct) : '-',
      hint: kpis.visits > 0 ? t('webActivity.overview.kpi.bounceHint') : t('webActivity.common.noVisitsRecorded'),
      tone: kpis.visits > 0 ? bounceTone(kpis.bouncePct) : 'neutral',
      info: {
        what: t('webActivity.overview.kpi.bounceWhat'),
        how: t('webActivity.overview.kpi.bounceHow'),
      },
    },
    {
      key: 'load',
      label: t('webActivity.overview.kpi.averagePageLoad'),
      value: formatSeconds(kpis.averageLoadSeconds),
      hint: t('webActivity.overview.kpi.averageTimeHint', { duration: formatDuration(kpis.averageSecondsOnPage) }),
      tone: loadTone(kpis.averageLoadSeconds),
      info: {
        what: t('webActivity.common.loadWhat'),
        how: t('webActivity.overview.kpi.loadHowPrefix', { caveat: dwellCaveatLower(t) }),
      },
    },
    {
      key: 'returning',
      label: t('webActivity.overview.kpi.seenBothHalves'),
      value: formatCount(kpis.returningVisitors),
      hint: t('webActivity.overview.kpi.firstSeenSecondHalfHint', { count: formatCount(kpis.newVisitors) }),
      info: {
        what: t('webActivity.overview.kpi.seenBothHalvesWhat'),
        how: t('webActivity.overview.kpi.seenBothHalvesHow'),
      },
    },
  ];

  const segmentCategories = translatedBucketsToCategories(t, 'visitorSegment', data.visitorSegments).filter((c) => c.value > 0);

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
          title={t('webActivity.overview.trafficOverTime.title')}
          description={t('webActivity.overview.trafficOverTime.description')}
          note={t('webActivity.overview.trend.note')}
          query={queryFor(data.queries, 'overview-trend')}
          isEmpty={data.trend.length === 0}
        >
          <TimeSeriesChart
            valueLabel={t('webActivity.common.count')}
            series={[
              { name: t('webActivity.common.pageViews'), points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.pageViews })) },
              { name: t('webActivity.common.visits'), points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.visits })) },
              { name: t('webActivity.common.visitors'), points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.visitors })) },
              {
                name: t('webActivity.common.singlePageVisits'),
                points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.bounces })),
              },
            ]}
          />
        </SectionCard>
      </div>

      <div className={styles.grid}>
        <SectionCard
          title={t('webActivity.overview.habit.title')}
          description={t('webActivity.overview.habit.description')}
          note={
            data.window.segmentsFullyReachable
              ? t('webActivity.overview.segments.noteFull')
              : t('webActivity.overview.segments.noteShort')
          }
          query={queryFor(data.queries, 'overview-segments')}
          isEmpty={segmentCategories.length === 0}
        >
          <DonutChart
            categories={segmentCategories}
            colours={segmentCategories.map((_, i) => seriesColor(i))}
            centreValue={formatCount(kpis.visitors)}
            centreLabel={t('webActivity.common.visitorsLower')}
          />
        </SectionCard>

        <SectionCard
          title={t('webActivity.overview.depth.title')}
          description={t('webActivity.common.visitDepthDescription')}
          query={queryFor(data.queries, 'overview-depth')}
          isEmpty={data.visitDepth.every((b) => b.count === 0)}
        >
          <CategoryBarChart
            categories={translatedBucketsToCategories(t, 'visitDepth', data.visitDepth)}
            valueLabel={t('webActivity.common.visits')}
            showShare
          />
        </SectionCard>

        <SectionCard
          title={t('webActivity.overview.busiestSites.title')}
          description={t('webActivity.overview.busiestSites.description')}
          note={t('webActivity.overview.busiestSites.note', { top: data.window.top })}
          query={queryFor(data.queries, 'overview-sites')}
          isEmpty={data.topSites.length === 0}
          emptyMessage={t('webActivity.overview.busiestSites.empty')}
        >
          <CategoryBarChart categories={toCategories(data.topSites)} valueLabel={t('webActivity.common.pageViews')} />
        </SectionCard>
      </div>

      <div className={styles.stack}>
        <SectionCard
          title={t('webActivity.overview.heatmap.title')}
          description={t('webActivity.overview.heatmap.description')}
          query={queryFor(data.queries, 'overview-heatmap')}
          isEmpty={data.heatmap.length === 0}
        >
          <HeatmapChart
            cells={data.heatmap.map((cell) => ({
              dayOfWeek: cell.day,
              hour: cell.hour,
              value: cell.pageViews,
            }))}
            valueLabel={t('webActivity.common.pageViewsLower')}
            footnote={t('webActivity.overview.heatmap.footnote')}
          />
        </SectionCard>
      </div>
    </div>
  );
}

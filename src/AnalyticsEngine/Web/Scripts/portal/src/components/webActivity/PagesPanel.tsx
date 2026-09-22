import {
  Button,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
} from '@fluentui/react-components';
import { ArrowDownload16Regular } from '@fluentui/react-icons';
import CategoryBarChart from '../charts/CategoryBarChart';
import StackedAreaChart from '../charts/StackedAreaChart';
import PageTable from './PageTable';
import { KpiGrid, type KpiDefinition } from '../shared/KpiGrid';
import type { WebActivityPages } from '../../types/webActivity';
import { useT } from '../../i18n';
import {
  FailedQueryNote,
  SectionCard,
  WindowNote,
  formatCount,
  formatDecimal,
  formatDuration,
  formatPct,
  formatSeconds,
  loadTone,
  queryFor,
  toStackedSeries,
  useWebActivityStyles,
} from './webActivityShared';

/**
 * The Page views tab - the in-app replacement for the Power BI report's "Page Views" page.
 *
 * Keeps total-versus-unique page views, the per-site split and the period-of-day breakdown over
 * time, and adds the two lists an intranet owner can act on directly: the slowest pages, and the
 * pages nobody reads.
 */
export default function PagesPanel({
  data,
  onExportPages,
  onExportQuiet,
  onExportSlow,
  exporting,
}: {
  data: WebActivityPages;
  onExportPages: () => void;
  onExportQuiet: () => void;
  onExportSlow: () => void;
  exporting: boolean;
}) {
  const styles = useWebActivityStyles();
  const t = useT();
  const kpis = data.kpis;

  const items: KpiDefinition[] = [
    {
      key: 'tpv',
      label: t('webActivity.pages.kpi.totalPageViews'),
      value: formatCount(kpis.pageViews),
      info: {
        what: t('webActivity.pages.kpi.totalPageViewsWhat'),
        how: t('webActivity.pages.kpi.totalPageViewsHow'),
      },
    },
    {
      key: 'upv',
      label: t('webActivity.pages.kpi.uniquePageViews'),
      value: formatCount(kpis.uniquePageViews),
      hint: t('webActivity.pages.kpi.ofTotalHint', { pct: formatPct(kpis.uniqueSharePct) }),
      info: {
        what: t('webActivity.pages.kpi.uniquePageViewsWhat'),
        how: t('webActivity.pages.kpi.uniquePageViewsHow'),
      },
    },
    {
      key: 'per-visit',
      label: t('webActivity.common.pagesPerVisit'),
      value: formatDecimal(kpis.pagesPerVisit),
      info: {
        what: t('webActivity.pages.kpi.pagesPerVisitWhat'),
        how: t('webActivity.pages.kpi.pagesPerVisitHow'),
      },
    },
    {
      key: 'dwell',
      label: t('webActivity.pages.kpi.averageTimeOnPage'),
      value: formatDuration(kpis.averageSecondsOnPage),
      info: {
        what: t('webActivity.pages.kpi.averageTimeOnPageWhat'),
        how: t('webActivity.pages.kpi.averageTimeHow'),
      },
    },
    {
      key: 'load',
      label: t('webActivity.pages.kpi.averageLoadTime'),
      value: formatSeconds(kpis.averageLoadSeconds),
      tone: loadTone(kpis.averageLoadSeconds),
      info: {
        what: t('webActivity.common.loadWhat'),
        how: t('webActivity.pages.kpi.averageLoadTimeHow'),
      },
    },
    {
      key: 'quiet',
      label: t('webActivity.pages.kpi.quietPages'),
      value: formatCount(kpis.quietPages),
      hint: t('webActivity.pages.kpi.quietPagesHint', { count: formatCount(kpis.uniquePages) }),
      tone: 'opportunity',
      info: {
        what: t('webActivity.pages.kpi.quietPagesWhat'),
        how: t('webActivity.pages.kpi.quietPagesHow'),
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
          title={t('webActivity.pages.mostViewed.title')}
          description={t('webActivity.pages.mostViewed.description', { top: data.window.top })}
          query={queryFor(data.queries, 'pages-top')}
          isEmpty={data.topPages.length === 0}
          actions={
            <Button
              appearance="subtle"
              size="small"
              icon={<ArrowDownload16Regular />}
              onClick={onExportPages}
              disabled={exporting}
            >
              {t('webActivity.common.export')}
            </Button>
          }
        >
          <PageTable
            rows={data.topPages}
            label={t('webActivity.pages.mostViewed.title')}
            columns={{ site: true, uniquePageViews: true, dwell: true, load: true, entries: true, bounce: true }}
            dwellFootnote
          />
        </SectionCard>

        <SectionCard
          title={t('webActivity.pages.slowest.title')}
          description={t('webActivity.pages.slowest.description', { minimumViews: data.window.minimumViews })}
          note={t('webActivity.pages.slowest.note')}
          query={queryFor(data.queries, 'pages-slowest')}
          isEmpty={data.slowestPages.length === 0}
          emptyMessage={t('webActivity.pages.slowest.empty')}
          actions={
            <Button
              appearance="subtle"
              size="small"
              icon={<ArrowDownload16Regular />}
              onClick={onExportSlow}
              disabled={exporting}
            >
              {t('webActivity.common.export')}
            </Button>
          }
        >
          <PageTable
            rows={data.slowestPages}
            label={t('webActivity.pages.slowest.title')}
            columns={{ site: true, uniquePageViews: false, dwell: false, load: true }}
          />
        </SectionCard>

        <SectionCard
          title={t('webActivity.pages.quiet.title')}
          description={t('webActivity.pages.quiet.description')}
          note={t('webActivity.pages.quiet.note')}
          query={queryFor(data.queries, 'pages-quiet')}
          isEmpty={data.quietPages.length === 0}
          emptyMessage={t('webActivity.pages.quiet.empty')}
          actions={
            <Button
              appearance="subtle"
              size="small"
              icon={<ArrowDownload16Regular />}
              onClick={onExportQuiet}
              disabled={exporting}
            >
              {t('webActivity.common.export')}
            </Button>
          }
        >
          <PageTable
            rows={data.quietPages}
            label={t('webActivity.pages.quiet.title')}
            columns={{ site: true, uniquePageViews: true, dwell: true, load: false }}
            dwellFootnote
          />
        </SectionCard>

        <SectionCard
          title={t('webActivity.pages.bySite.title')}
          query={queryFor(data.queries, 'pages-sites')}
          isEmpty={data.bySite.length === 0}
        >
          <div className={styles.tableWrap}>
            <Table size="small" aria-label={t('webActivity.pages.bySite.aria')}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>{t('webActivity.common.site')}</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>{t('webActivity.common.pageViews')}</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>{t('webActivity.common.unique')}</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>{t('webActivity.common.visits')}</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>{t('webActivity.common.visitors')}</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>{t('webActivity.common.pagesPerVisitShort')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.bySite.map((site) => (
                  <TableRow key={site.url ?? site.name}>
                    <TableCell className={styles.td}>
                      <div className={styles.ellipsis} title={site.url ?? site.name}>
                        {site.name}
                      </div>
                    </TableCell>
                    <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(site.pageViews)}</TableCell>
                    <TableCell className={`${styles.td} ${styles.numeric}`}>
                      {formatCount(site.uniquePageViews)}
                    </TableCell>
                    <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(site.visits)}</TableCell>
                    <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(site.visitors)}</TableCell>
                    <TableCell className={`${styles.td} ${styles.numeric}`}>
                      {site.visits > 0 ? formatDecimal(site.visitPageViews / site.visits) : '\u2014'}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </SectionCard>

        <SectionCard
          title={t('webActivity.pages.readOverTime.title')}
          description={t('webActivity.pages.readOverTime.description')}
          query={queryFor(data.queries, 'pages-period-over-time')}
          isEmpty={data.periodOverTime.length === 0}
        >
          <StackedAreaChart series={toStackedSeries(data.periodOverTime)} valueLabel={t('webActivity.common.pageViews')} />
        </SectionCard>
      </div>

      <div className={styles.grid}>
        <SectionCard
          title={t('webActivity.pages.concentration.title')}
          description={t('webActivity.pages.concentration.description')}
          query={queryFor(data.queries, 'pages-distribution')}
          isEmpty={kpis.uniquePages < data.window.minimumPagesForDecile}
          emptyMessage={
            kpis.uniquePages === 0
              ? t('webActivity.pages.concentration.emptyNoPages')
              : t('webActivity.pages.concentration.emptyTooFew', { count: data.window.minimumPagesForDecile })
          }
        >
          <CategoryBarChart
            valueLabel={t('webActivity.pages.concentration.valueLabel')}
            categories={[
              { label: t('webActivity.pages.concentration.busiestTenth'), value: Math.round(kpis.topDecilePagePct * 10) / 10 },
              { label: t('webActivity.pages.concentration.everythingElse'), value: Math.round((100 - kpis.topDecilePagePct) * 10) / 10 },
            ]}
          />
        </SectionCard>
      </div>
    </div>
  );
}

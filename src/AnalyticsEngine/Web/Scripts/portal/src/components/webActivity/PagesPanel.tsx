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
import {
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
  const kpis = data.kpis;

  const items: KpiDefinition[] = [
    {
      key: 'tpv',
      label: 'Total page views',
      value: formatCount(kpis.pageViews),
      info: {
        what: 'Every page view recorded in the period.',
        how: 'One row of dbo.hits is one page view.',
      },
    },
    {
      key: 'upv',
      label: 'Unique page views',
      value: formatCount(kpis.uniquePageViews),
      hint: `${formatPct(kpis.uniqueSharePct)} of total`,
      info: {
        what: 'A page counted once per visit, however many times it was viewed in that visit.',
        how:
          'Distinct (visit, page) pairs - the definition Google Analytics uses. A low share of total '
          + 'means people re-open or refresh the same pages a lot within a visit, which is often a '
          + 'sign of a page that is a jumping-off point rather than a destination.',
      },
    },
    {
      key: 'per-visit',
      label: 'Pages per visit',
      value: formatDecimal(kpis.pagesPerVisit),
      info: {
        what: 'Average number of pages seen in a visit.',
        how: 'Total page views divided by total visits.',
      },
    },
    {
      key: 'dwell',
      label: 'Average time on page',
      value: formatDuration(kpis.averageSecondsOnPage),
      info: {
        what: 'How long a page was open before the visitor moved on.',
        how:
          'Mean of the per-view dwell time the tracker reports. It cannot measure the LAST page of a '
          + 'visit - there is no next page view to measure against - so exit pages are '
          + 'systematically under-represented in this figure.',
      },
    },
    {
      key: 'load',
      label: 'Average load time',
      value: formatSeconds(kpis.averageLoadSeconds),
      tone: loadTone(kpis.averageLoadSeconds),
      info: {
        what: 'How long a page took to become usable, as the browser measured it.',
        how: 'Mean of the browser-reported page load time, in seconds. Views with no reported time are excluded.',
      },
    },
    {
      key: 'quiet',
      label: 'Quiet pages',
      value: formatCount(kpis.quietPages),
      hint: `of ${formatCount(kpis.uniquePages)} pages viewed at all`,
      tone: 'opportunity',
      info: {
        what: 'Pages viewed three times or fewer in the whole period.',
        how:
          'These are your content-cleanup backlog. Note the denominator: it is pages that were '
          + 'viewed AT LEAST ONCE. A page nobody opened at all never appears in the page-view data, '
          + 'so this list under-counts genuinely dead content rather than over-counting it.',
      },
    },
  ];

  return (
    <div>
      <div style={{ marginTop: '12px' }}>
        <WindowNote window={data.window} />
      </div>

      <div style={{ marginTop: '12px' }}>
        <KpiGrid items={items} />
      </div>

      <div className={styles.stack}>
        <SectionCard
          title="Most viewed pages"
          description={`Top ${data.window.top} by page views, with how long people stayed and how many entered the site there.`}
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
              Export
            </Button>
          }
        >
          <PageTable
            rows={data.topPages}
            columns={{ site: true, uniquePageViews: true, dwell: true, load: true, entries: true, bounce: true }}
          />
        </SectionCard>

        <SectionCard
          title="Slowest pages"
          description={`Pages with at least ${data.window.minimumViews} views, ranked by average load time.`}
          note={
            'A view floor is applied deliberately. Without one this table is always topped by a page '
            + 'that was opened once, slowly - noise presented as a finding.'
          }
          query={queryFor(data.queries, 'pages-slowest')}
          isEmpty={data.slowestPages.length === 0}
          emptyMessage="No page has enough views with a recorded load time to rank."
          actions={
            <Button
              appearance="subtle"
              size="small"
              icon={<ArrowDownload16Regular />}
              onClick={onExportSlow}
              disabled={exporting}
            >
              Export
            </Button>
          }
        >
          <PageTable
            rows={data.slowestPages}
            columns={{ site: true, uniquePageViews: false, dwell: false, load: true }}
          />
        </SectionCard>

        <SectionCard
          title="Pages nobody reads"
          description="Viewed three times or fewer in the whole period - the cheapest content-cleanup backlog you will get."
          note={
            'Sorted least-viewed first and then by URL, so the list is stable between refreshes and '
            + 'can be worked through. Check a page before retiring it: a seldom-read page that is '
            + 'legally required to exist is not a pruning candidate.'
          }
          query={queryFor(data.queries, 'pages-quiet')}
          isEmpty={data.quietPages.length === 0}
          emptyMessage="Every page that was viewed at all was viewed more than three times."
          actions={
            <Button
              appearance="subtle"
              size="small"
              icon={<ArrowDownload16Regular />}
              onClick={onExportQuiet}
              disabled={exporting}
            >
              Export
            </Button>
          }
        >
          <PageTable
            rows={data.quietPages}
            columns={{ site: true, uniquePageViews: true, dwell: true, load: false }}
          />
        </SectionCard>

        <SectionCard
          title="Total and unique page views by site"
          query={queryFor(data.queries, 'pages-sites')}
          isEmpty={data.bySite.length === 0}
        >
          <div className={styles.tableWrap}>
            <Table size="small" aria-label="Page views by site">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>Site</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>Page views</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>Unique</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>Visits</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>Visitors</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>Pages / visit</TableHeaderCell>
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
                      {site.visits > 0 ? formatDecimal(site.pageViews / site.visits) : '\u2014'}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </SectionCard>

        <SectionCard
          title="When pages are read, over time"
          description="Weekly page views split by period of the day (UTC)."
          query={queryFor(data.queries, 'pages-period-over-time')}
          isEmpty={data.periodOverTime.length === 0}
        >
          <StackedAreaChart series={toStackedSeries(data.periodOverTime)} valueLabel="Page views" />
        </SectionCard>
      </div>

      <div className={styles.grid}>
        <SectionCard
          title="How concentrated the traffic is"
          description="The share of all page views taken by the busiest tenth of pages."
          query={queryFor(data.queries, 'pages-distribution')}
          isEmpty={kpis.uniquePages === 0}
        >
          <CategoryBarChart
            valueLabel="Share of page views (%)"
            categories={[
              { label: 'Busiest 10% of pages', value: Math.round(kpis.topDecilePagePct * 10) / 10 },
              { label: 'Everything else', value: Math.round((100 - kpis.topDecilePagePct) * 10) / 10 },
            ]}
          />
        </SectionCard>
      </div>
    </div>
  );
}

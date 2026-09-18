import {
  Button,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
} from '@fluentui/react-components';
import { ArrowDownload16Regular, ArrowRight16Regular } from '@fluentui/react-icons';
import CategoryBarChart from '../charts/CategoryBarChart';
import PageTable from './PageTable';
import { KpiGrid, type KpiDefinition } from '../shared/KpiGrid';
import type { WebActivityJourneys } from '../../types/webActivity';
import {
  FailedQueryNote,
  SectionCard,
  WindowNote,
  bounceTone,
  bucketsToCategories,
  formatCount,
  formatDecimal,
  formatDuration,
  formatPct,
  queryFor,
  shortenUrl,
  toCategories,
  useWebActivityStyles,
} from './webActivityShared';

/**
 * The Journeys tab - where people arrive, where they give up, and the routes between.
 *
 * This tab has no Power BI equivalent; it is the one an intranet owner needs most and never had.
 * "Home page, then nothing" is a landing-page problem, "News, then Policies, then out" is a working
 * navigation path, and the only way to tell them apart is to look at the sequence.
 */
export default function JourneysPanel({
  data,
  clickTrackingAvailable,
  onExportEntry,
  onExportExit,
  onExportTransitions,
  exporting,
}: {
  data: WebActivityJourneys;
  clickTrackingAvailable: boolean;
  onExportEntry: () => void;
  onExportExit: () => void;
  onExportTransitions: () => void;
  exporting: boolean;
}) {
  const styles = useWebActivityStyles();
  const kpis = data.kpis;

  const items: KpiDefinition[] = [
    {
      key: 'bounce',
      label: 'Bounce rate',
      value: formatPct(kpis.bouncePct),
      hint: `${formatCount(kpis.bounces)} of ${formatCount(kpis.visits)} visits`,
      tone: bounceTone(kpis.bouncePct),
      info: {
        what: 'Visits that saw exactly one page and then ended.',
        how:
          'Single-page visits over all visits. Judge it per page, not in aggregate: a deep link '
          + 'straight to one policy document is a perfectly good one-page visit, while a home page '
          + 'with the same rate is a navigation failure.',
      },
    },
    {
      key: 'pages-per-visit',
      label: 'Pages per visit',
      value: formatDecimal(kpis.pagesPerVisit),
      hint: `median ${formatCount(kpis.medianPagesPerVisit)}`,
      info: {
        what: 'How many pages a visit sees, on average and at the midpoint.',
        how:
          'The median is shown next to the mean because a handful of very deep visits - a content '
          + 'author clicking through fifty pages - drags the mean up and the median does not move. '
          + 'When the two are far apart, trust the median.',
      },
    },
    {
      key: 'visit-length',
      label: 'Average visit length',
      value: formatDuration(kpis.averageVisitSeconds),
      info: {
        what: 'Total time a visit spent on pages.',
        how:
          'The per-page dwell times in the visit, summed. The tracker cannot measure the last page '
          + 'of a visit, so this under-states real visit length - consistently, so it is still '
          + 'usable for comparing periods.',
      },
    },
    {
      key: 'clicks',
      label: 'Element clicks',
      value: formatCount(kpis.clicks),
      hint: clickTrackingAvailable ? 'recorded in this period' : 'click capture is off',
      info: {
        what: 'Clicks on tracked page elements - links, buttons, web parts.',
        how:
          'Optional: the SharePoint tracker only records these when element-click capture is '
          + 'enabled, so zero usually means the feature is off rather than that nobody clicked '
          + 'anything.',
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
          title="Where visits start"
          description="The first page of a visit, with how often the visit ended right there."
          query={queryFor(data.queries, 'journeys-entry')}
          isEmpty={data.entryPages.length === 0}
          actions={
            <Button
              appearance="subtle"
              size="small"
              icon={<ArrowDownload16Regular />}
              onClick={onExportEntry}
              disabled={exporting}
            >
              Export
            </Button>
          }
        >
          <PageTable
            rows={data.entryPages}
            valueHeading="Entries"
            columns={{ site: true, uniquePageViews: false, dwell: true, bounce: true }}
            dwellFootnote
          />
        </SectionCard>

        <SectionCard
          title="Landing pages people leave from"
          description={`Entry pages with at least ${data.window.minimumViews} entries, ranked by how often the visit ended there.`}
          note={
            'These are the pages costing you the most traffic. Each one is either a page that '
            + 'answered the question completely - which is fine - or a dead end that should be '
            + 'offering a next step.'
          }
          query={queryFor(data.queries, 'journeys-entry')}
          isEmpty={data.bouncePages.length === 0}
          emptyMessage="No entry page had enough entries to rank by bounce rate."
        >
          <PageTable
            rows={data.bouncePages}
            valueHeading="Entries"
            columns={{ site: true, uniquePageViews: false, dwell: false, bounce: true }}
          />
        </SectionCard>

        <SectionCard
          title="Where visits end"
          description="The last page of a visit."
          note={
            'No average time is shown here. The tracker measures dwell time against the NEXT page '
            + 'view, and by definition these pages have none - so the figure would be an artefact, '
            + 'not a measurement.'
          }
          query={queryFor(data.queries, 'journeys-exit')}
          isEmpty={data.exitPages.length === 0}
          actions={
            <Button
              appearance="subtle"
              size="small"
              icon={<ArrowDownload16Regular />}
              onClick={onExportExit}
              disabled={exporting}
            >
              Export
            </Button>
          }
        >
          <PageTable
            rows={data.exitPages}
            valueHeading="Exits"
            columns={{ site: true, uniquePageViews: false, dwell: false }}
          />
        </SectionCard>

        <SectionCard
          title="The routes people take"
          description="The most-walked steps from one page to the next inside a visit."
          note={
            'A step back to the SAME page is excluded, so a refresh cannot fill this table with '
            + '"Home to Home" and bury the real paths. The share column is of all steps taken OUT of '
            + 'the left-hand page, so it answers "where do people go from here?".'
          }
          query={queryFor(data.queries, 'journeys-transitions')}
          isEmpty={data.transitions.length === 0}
          emptyMessage="No visit in this period saw two different pages, so there are no journeys to show."
          actions={
            <Button
              appearance="subtle"
              size="small"
              icon={<ArrowDownload16Regular />}
              onClick={onExportTransitions}
              disabled={exporting}
            >
              Export
            </Button>
          }
        >
          <div className={styles.tableWrap}>
            <Table size="small" aria-label="Page journeys">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>From</TableHeaderCell>
                  <TableHeaderCell />
                  <TableHeaderCell>To</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>Times</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>Share of exits from</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.transitions.map((step) => (
                  <TableRow key={`${step.fromUrl}\u0000${step.toUrl}`}>
                    <TableCell className={styles.td}>
                      <div className={styles.ellipsis} title={step.fromUrl}>
                        {step.fromTitle || shortenUrl(step.fromUrl)}
                      </div>
                    </TableCell>
                    <TableCell className={styles.td}>
                      <ArrowRight16Regular />
                    </TableCell>
                    <TableCell className={styles.td}>
                      <div className={styles.ellipsis} title={step.toUrl}>
                        {step.toTitle || shortenUrl(step.toUrl)}
                      </div>
                    </TableCell>
                    <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(step.count)}</TableCell>
                    <TableCell className={`${styles.td} ${styles.numeric}`}>{formatPct(step.sharePct)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </SectionCard>
      </div>

      <div className={styles.grid}>
        <SectionCard
          title="How deep visits go"
          description="Visits by the number of pages they saw."
          query={queryFor(data.queries, 'journeys-depth')}
          isEmpty={data.depth.every((b) => b.count === 0)}
        >
          <CategoryBarChart categories={bucketsToCategories(data.depth)} valueLabel="Visits" showShare />
        </SectionCard>

        <SectionCard
          title="What visitors clicked"
          description="Tracked page elements, by click count."
          query={queryFor(data.queries, 'journeys-clicks')}
          isEmpty={data.clickedElements.length === 0}
          emptyMessage={
            clickTrackingAvailable
              ? 'No element clicks were recorded in this period.'
              : 'Element-click capture is not switched on in the SharePoint tracker, so there is nothing to show here. Only this panel depends on it.'
          }
        >
          <CategoryBarChart categories={toCategories(data.clickedElements)} valueLabel="Clicks" />
        </SectionCard>
      </div>

      <Text size={200} className={styles.muted} style={{ marginTop: '16px', display: 'block' }}>
        Journeys are reconstructed from the order of page views inside a visit. A visit that started
        before this period began is measured from its first page view inside it, so two adjacent
        periods never double-count the same visit.
      </Text>
    </div>
  );
}

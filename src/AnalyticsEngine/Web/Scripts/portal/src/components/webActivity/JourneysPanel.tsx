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
import SankeyChart from '../charts/SankeyChart';
import PageTable from './PageTable';
import { KpiGrid, type KpiDefinition } from '../shared/KpiGrid';
import type { WebActivityJourneys } from '../../types/webActivity';
import { useT } from '../../i18n';
import {
  FailedQueryNote,
  SectionCard,
  WindowNote,
  bounceTone,
  bucketsToCategories,
  translatedBucketsToCategories,
  formatCount,
  formatDecimal,
  formatDuration,
  formatPct,
  flooredCard,
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
  onExportFlows,
  exporting,
}: {
  data: WebActivityJourneys;
  clickTrackingAvailable: boolean;
  onExportEntry: () => void;
  onExportExit: () => void;
  onExportTransitions: () => void;
  onExportFlows: () => void;
  exporting: boolean;
}) {
  const styles = useWebActivityStyles();
  const t = useT();
  const kpis = data.kpis;

  // Without See PII, a page, step, flow or clicked element of too few people is left out of each list, with the smallest
  // row shown. Each card says so - and when every row is left out, says that instead of "nothing happened".
  const floored = (leftOut: number | undefined, rows: number, own: { note?: string; empty?: string } = {}) =>
    flooredCard(t, data.minimumGroupSize, leftOut, rows, own);
  const entry = floored(data.withheldEntryPages, data.entryPages.length);
  const bounce = floored(data.withheldBouncePages, data.bouncePages.length, {
    note: t('webActivity.journeys.bounce.note'),
    empty: t('webActivity.journeys.bouncePages.empty'),
  });
  const exit = floored(data.withheldExitPages, data.exitPages.length, { note: t('webActivity.journeys.exit.note') });
  const flows = floored(data.withheldFlows, data.flows.length, {
    note: t('webActivity.journeys.flows.note'),
    empty: t('webActivity.journeys.flows.empty'),
  });
  const routes = floored(data.withheldTransitions, data.transitions.length, {
    note: t('webActivity.journeys.transitions.note'),
    empty: t('webActivity.journeys.routes.empty'),
  });
  const clicks = floored(data.withheldClickedElements, data.clickedElements.length, {
    empty: clickTrackingAvailable ? t('webActivity.journeys.clicks.emptyThisPeriod') : t('webActivity.journeys.clicks.emptyEver'),
  });
  const exportButton = (exportable: boolean, onClick: () => void) =>
    exportable ? (
      <Button appearance="subtle" size="small" icon={<ArrowDownload16Regular />} onClick={onClick} disabled={exporting}>
        {t('webActivity.common.export')}
      </Button>
    ) : undefined;

  const items: KpiDefinition[] = [
    {
      key: 'bounce',
      label: t('webActivity.journeys.kpi.bounceRate'),
      value: kpis.visits > 0 ? formatPct(kpis.bouncePct) : '-',
      hint: kpis.visits > 0
        ? t('webActivity.journeys.kpi.bouncesOfVisitsHint', { bounces: formatCount(kpis.bounces), visits: formatCount(kpis.visits) })
        : t('webActivity.common.noVisitsRecorded'),
      tone: kpis.visits > 0 ? bounceTone(kpis.bouncePct) : 'neutral',
      info: {
        what: t('webActivity.journeys.kpi.bounceWhat'),
        how: t('webActivity.journeys.kpi.bounceHow'),
      },
    },
    {
      key: 'pages-per-visit',
      label: t('webActivity.common.pagesPerVisit'),
      value: formatDecimal(kpis.pagesPerVisit),
      hint: t('webActivity.journeys.kpi.medianHint', { count: formatCount(kpis.medianPagesPerVisit) }),
      info: {
        what: t('webActivity.journeys.kpi.pagesPerVisitWhat'),
        how: t('webActivity.journeys.kpi.pagesPerVisitHow'),
      },
    },
    {
      key: 'visit-length',
      label: t('webActivity.journeys.kpi.averageVisitLength'),
      value: formatDuration(kpis.averageVisitSeconds),
      hint: kpis.visits === 0
        ? t('webActivity.common.noVisitsRecorded')
        : kpis.averageVisitSeconds === null
          ? t('webActivity.journeys.kpi.noMeasuredVisitsHint')
          : t('webActivity.journeys.kpi.measuredVisitsHint', {
              measured: formatCount(kpis.measuredVisits),
              visits: formatCount(kpis.visits),
            }),
      info: {
        what: t('webActivity.journeys.kpi.averageVisitLengthWhat'),
        how: t('webActivity.journeys.kpi.visitLengthHow'),
      },
    },
    {
      key: 'clicks',
      label: t('webActivity.common.elementClicks'),
      value: formatCount(kpis.clicks),
      hint: clickTrackingAvailable ? t('webActivity.journeys.kpi.clicksRecordedHint') : t('webActivity.journeys.kpi.noClicksHint'),
      info: {
        what: t('webActivity.journeys.kpi.clicksWhat'),
        how: t('webActivity.journeys.kpi.clicksHow'),
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
          title={t('webActivity.journeys.entry.title')}
          description={t('webActivity.journeys.entry.description')}
          note={entry.note}
          query={queryFor(data.queries, 'journeys-entry')}
          isEmpty={data.entryPages.length === 0}
          emptyMessage={entry.emptyMessage}
          actions={exportButton(entry.exportable, onExportEntry)}
        >
          <PageTable
            rows={data.entryPages}
            label={t('webActivity.journeys.entry.title')}
            valueHeading={t('webActivity.common.entries')}
            columns={{ site: true, uniquePageViews: false, dwell: true, bounce: true }}
            dwellFootnote
          />
        </SectionCard>

        <SectionCard
          title={t('webActivity.journeys.bouncePages.title')}
          description={t('webActivity.journeys.bouncePages.description', { minimumViews: data.window.minimumViews })}
          note={bounce.note}
          query={queryFor(data.queries, 'journeys-bounce')}
          isEmpty={data.bouncePages.length === 0}
          emptyMessage={bounce.emptyMessage}
        >
          <PageTable
            rows={data.bouncePages}
            label={t('webActivity.journeys.bouncePages.title')}
            valueHeading={t('webActivity.common.entries')}
            columns={{ site: true, uniquePageViews: false, dwell: false, bounce: true }}
          />
        </SectionCard>

        <SectionCard
          title={t('webActivity.journeys.exit.title')}
          description={t('webActivity.journeys.exit.description')}
          note={exit.note}
          query={queryFor(data.queries, 'journeys-exit')}
          isEmpty={data.exitPages.length === 0}
          emptyMessage={exit.emptyMessage}
          actions={exportButton(exit.exportable, onExportExit)}
        >
          <PageTable
            rows={data.exitPages}
            label={t('webActivity.journeys.exit.title')}
            valueHeading={t('webActivity.common.exits')}
            columns={{ site: true, uniquePageViews: false, dwell: false }}
          />
        </SectionCard>

        <SectionCard
          title={t('webActivity.journeys.flows.title')}
          description={t('webActivity.journeys.flows.description')}
          note={flows.note}
          query={queryFor(data.queries, 'journeys-flows')}
          isEmpty={data.flows.length === 0}
          emptyMessage={flows.emptyMessage}
          actions={exportButton(flows.exportable, onExportFlows)}
        >
          <SankeyChart
            flows={data.flows.map((f) => ({
              sourceKey: f.startUrl,
              sourceLabel: f.startTitle,
              targetKey: f.endUrl,
              targetLabel: f.endTitle,
              value: f.visits,
              isSelfFlow: f.endedWhereItStarted,
              detail: [
                f.endedWhereItStarted ? t('webActivity.journeys.flows.endedWhereStarted') : '',
                t('webActivity.journeys.flows.shareOfAllVisits', { pct: formatPct(f.sharePct) }),
                f.averagePages != null ? t('webActivity.journeys.flows.pagesOnAverage', { count: formatDecimal(f.averagePages) }) : '',
                f.endedWhereItStarted && f.singlePageVisits > 0
                  ? t('webActivity.journeys.flows.sawOnlyThisPage', { count: formatCount(f.singlePageVisits) })
                  : '',
              ].filter((d) => d !== ''),
            }))}
            valueLabel={t('webActivity.common.visitsLower')}
            caption={
              t('webActivity.journeys.flows.caption', { count: formatCount(data.flows.length), pct: formatPct(data.flowsCoveragePct) })
            }
          />
        </SectionCard>

        <SectionCard
          title={t('webActivity.journeys.routes.title')}
          description={t('webActivity.journeys.routes.description')}
          note={routes.note}
          query={queryFor(data.queries, 'journeys-transitions')}
          isEmpty={data.transitions.length === 0}
          emptyMessage={routes.emptyMessage}
          actions={exportButton(routes.exportable, onExportTransitions)}
        >
          <div className={styles.tableWrap}>
            <Table size="small" aria-label={t('webActivity.journeys.routes.aria')}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>{t('webActivity.journeys.routes.from')}</TableHeaderCell>
                  <TableHeaderCell>{t('webActivity.journeys.routes.step')}</TableHeaderCell>
                  <TableHeaderCell>{t('webActivity.journeys.routes.to')}</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>{t('webActivity.journeys.routes.times')}</TableHeaderCell>
                  <TableHeaderCell className={styles.numeric}>{t('webActivity.journeys.routes.shareOfExitsFrom')}</TableHeaderCell>
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
                      {/* Decorative: the From and To columns already carry the meaning, and the
                          glyph does not mirror under a right-to-left locale. */}
                      <ArrowRight16Regular aria-hidden="true" />
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
          title={t('webActivity.journeys.depth.title')}
          description={t('webActivity.common.visitDepthDescription')}
          query={queryFor(data.queries, 'journeys-depth')}
          isEmpty={data.depth.every((b) => b.count === 0)}
        >
          <CategoryBarChart categories={translatedBucketsToCategories(t, 'visitDepth', data.depth)} valueLabel={t('webActivity.common.visits')} showShare />
        </SectionCard>

        <SectionCard
          title={t('webActivity.journeys.clicks.title')}
          description={t('webActivity.journeys.clicks.description')}
          note={clicks.note}
          query={queryFor(data.queries, 'journeys-clicks')}
          isEmpty={data.clickedElements.length === 0}
          emptyMessage={clicks.emptyMessage}
        >
          <CategoryBarChart categories={toCategories(data.clickedElements)} valueLabel={t('webActivity.journeys.clicks.valueLabel')} />
        </SectionCard>
      </div>

      <Text size={200} className={styles.muted} style={{ marginTop: '16px', display: 'block' }}>
        {t('webActivity.journeys.footer')}
      </Text>
    </div>
  );
}

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
import TimeSeriesChart from '../charts/TimeSeriesChart';
import WordCloud from '../charts/WordCloud';
import { KpiGrid, type KpiDefinition } from '../shared/KpiGrid';
import type { WebActivitySearch, WebActivitySearchTermRow } from '../../types/webActivity';
import { useT } from '../../i18n';
import {
  FailedQueryNote,
  SectionCard,
  WindowNote,
  bucketsToCategories,
  translatedBucketsToCategories,
  formatCount,
  formatDecimal,
  formatPct,
  queryFor,
  searchRelianceTone,
  toCategories,
  useWebActivityStyles,
} from './webActivityShared';

/**
 * The Web searches tab - the in-app replacement for the Power BI report's search page.
 *
 * Keeps the term leaderboard and the time breakdowns, and adds the measure that makes the tab
 * actionable rather than merely interesting: how often a search recorded no further page view.
 * Top search terms are a demand signal; terms that repeatedly record nothing after them are worth
 * investigating - though the measure is an upper bound, not a count of failed searches.
 */
export default function SearchPanel({
  data,
  searchAvailable,
  onExportTerms,
  exporting,
}: {
  data: WebActivitySearch;
  searchAvailable: boolean;
  onExportTerms: () => void;
  exporting: boolean;
}) {
  const styles = useWebActivityStyles();
  const t = useT();
  const kpis = data.kpis;

  const items: KpiDefinition[] = [
    {
      key: 'searches',
      label: t('webActivity.common.searches'),
      value: formatCount(kpis.searches),
      hint: t('webActivity.search.kpi.distinctTermsHint', { count: formatCount(kpis.terms) }),
      info: {
        what: t('webActivity.search.kpi.searchesWhat'),
        how: t('webActivity.search.kpi.searchesHow'),
      },
    },
    {
      key: 'searchers',
      label: t('webActivity.search.kpi.peopleWhoSearched'),
      value: formatCount(kpis.searchers),
      info: { what: t('webActivity.search.kpi.peopleWhoSearchedWhat'), how: t('webActivity.search.kpi.peopleWhoSearchedHow') },
    },
    {
      key: 'reliance',
      label: t('webActivity.search.kpi.visitsThatSearched'),
      value: formatPct(kpis.searchReliancePct),
      hint: t('webActivity.common.visitsCount', { count: formatCount(kpis.sessionsWithSearch) }),
      tone: searchRelianceTone(kpis.searchReliancePct),
      info: {
        what: t('webActivity.search.kpi.visitsThatSearchedWhat'),
        how: t('webActivity.search.kpi.relianceHow'),
      },
    },
    {
      key: 'per-visit',
      label: t('webActivity.search.kpi.searchesPerSearchingVisit'),
      value: formatDecimal(kpis.searchesPerSearchingVisit),
      hint: t('webActivity.search.kpi.strugglingVisitsHint', { count: formatCount(kpis.strugglingVisits) }),
      info: {
        what: t('webActivity.search.kpi.searchesPerSearchingVisitWhat'),
        how: t('webActivity.search.kpi.perVisitHow'),
      },
    },
    {
      key: 'dead-ends',
      label: t('webActivity.search.kpi.searchesLeadingNowhere'),
      value: formatPct(kpis.deadEndPct),
      hint: t('webActivity.common.searchesCount', { count: formatCount(kpis.deadEndSearches) }),
      tone: kpis.deadEndPct >= 50 ? 'warning' : 'neutral',
      info: {
        what: t('webActivity.search.kpi.searchesLeadingNowhereWhat'),
        how: t('webActivity.search.kpi.deadEndsHow', { seconds: data.kpis.deadEndGraceSeconds }),
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
          title={t('webActivity.search.wordCloud.title')}
          description={t('webActivity.search.wordCloud.description')}
          query={queryFor(data.queries, 'search-terms')}
          isEmpty={data.topTerms.length === 0}
          emptyMessage={
            searchAvailable
              ? t('webActivity.search.wordCloud.emptyThisPeriod')
              : t('webActivity.search.wordCloud.emptyEver')
          }
        >
          <WordCloud
            categories={data.topTerms.map((t) => ({ label: t.term, value: t.searches }))}
            valueLabel={t('webActivity.common.searches')}
          />
        </SectionCard>

        <SectionCard
          title={t('webActivity.search.topTerms.title')}
          description={t('webActivity.search.topTerms.description', { top: data.window.top })}
          query={queryFor(data.queries, 'search-terms')}
          isEmpty={data.topTerms.length === 0}
          actions={
            <Button
              appearance="subtle"
              size="small"
              icon={<ArrowDownload16Regular />}
              onClick={onExportTerms}
              disabled={exporting}
            >
              {t('webActivity.common.export')}
            </Button>
          }
        >
          <TermTable rows={data.topTerms} label={t('webActivity.search.topTerms.title')} />
        </SectionCard>

        <SectionCard
          title={t('webActivity.search.deadEndTerms.title')}
          description={t('webActivity.search.deadEndTerms.description')}
          note={t('webActivity.search.deadTerms.note')}
          query={queryFor(data.queries, 'search-dead-ends')}
          isEmpty={data.deadEndTerms.length === 0}
          emptyMessage={t('webActivity.search.deadEndTerms.empty')}
        >
          <TermTable rows={data.deadEndTerms} hideSearchers label={t('webActivity.search.deadEndTerms.label')} />
        </SectionCard>

        <SectionCard
          title={t('webActivity.search.trend.title')}
          query={queryFor(data.queries, 'search-trend')}
          isEmpty={data.trend.length === 0}
        >
          <TimeSeriesChart
            valueLabel={t('webActivity.common.searches')}
            series={[
              { name: t('webActivity.common.searches'), points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.searches })) },
            ]}
          />
        </SectionCard>
      </div>

      <div className={styles.grid}>
        <SectionCard
          title={t('webActivity.search.byDay.title')}
          query={queryFor(data.queries, 'search-day-hour')}
          isEmpty={data.byDay.every((b) => b.count === 0)}
        >
          <CategoryBarChart categories={translatedBucketsToCategories(t, 'day', data.byDay)} valueLabel={t('webActivity.common.searches')} showShare />
        </SectionCard>

        <SectionCard
          title={t('webActivity.search.byPeriod.title')}
          description={t('webActivity.common.utc')}
          query={queryFor(data.queries, 'search-day-hour')}
          isEmpty={data.byPeriodOfDay.every((b) => b.count === 0)}
        >
          <CategoryBarChart categories={translatedBucketsToCategories(t, 'period', data.byPeriodOfDay)} valueLabel={t('webActivity.common.searches')} showShare />
        </SectionCard>

        <SectionCard
          title={t('webActivity.search.bySite.title')}
          description={t('webActivity.search.bySite.description')}
          note={t('webActivity.search.bySite.note')}
          query={queryFor(data.queries, 'search-sites')}
          isEmpty={data.bySite.length === 0}
        >
          <CategoryBarChart categories={toCategories(data.bySite)} valueLabel={t('webActivity.common.searches')} />
        </SectionCard>
      </div>
    </div>
  );
}

function TermTable({
  rows,
  hideSearchers,
  label,
}: {
  rows: WebActivitySearchTermRow[];
  hideSearchers?: boolean;
  label: string;
}) {
  const styles = useWebActivityStyles();
  const t = useT();

  return (
    <div className={styles.tableWrap}>
      <Table size="small" aria-label={label}>
        <TableHeader>
          <TableRow>
            <TableHeaderCell>{t('webActivity.search.termTable.term')}</TableHeaderCell>
            <TableHeaderCell className={styles.numeric}>{t('webActivity.common.searches')}</TableHeaderCell>
            {!hideSearchers && <TableHeaderCell className={styles.numeric}>{t('webActivity.search.termTable.people')}</TableHeaderCell>}
            <TableHeaderCell className={styles.numeric}>{t('webActivity.search.termTable.ledNowhere')}</TableHeaderCell>
            <TableHeaderCell className={styles.numeric}>{t('webActivity.common.share')}</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row) => (
            <TableRow key={row.term}>
              <TableCell className={styles.td}>
                <div className={styles.ellipsis} title={row.term}>
                  {row.term}
                </div>
              </TableCell>
              <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(row.searches)}</TableCell>
              {!hideSearchers && (
                <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(row.searchers)}</TableCell>
              )}
              <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(row.deadEnds)}</TableCell>
              <TableCell className={`${styles.td} ${styles.numeric}`}>{formatPct(row.deadEndPct)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

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
import {
  FailedQueryNote,
  SectionCard,
  WindowNote,
  bucketsToCategories,
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
  const kpis = data.kpis;

  const items: KpiDefinition[] = [
    {
      key: 'searches',
      label: 'Searches',
      value: formatCount(kpis.searches),
      hint: `${formatCount(kpis.terms)} distinct terms`,
      info: {
        what: 'Searches run from the SharePoint sites the tracker covers.',
        how:
          'One row of dbo.searches is one search. Searches imported before the search fix migration '
          + 'have no timestamp and cannot be attributed to any period, so they are excluded '
          + 'everywhere rather than being dropped into whichever window happens to be open.',
      },
    },
    {
      key: 'searchers',
      label: 'People who searched',
      value: formatCount(kpis.searchers),
      info: { what: 'Distinct people who ran at least one search.', how: 'Distinct users behind the searching sessions.' },
    },
    {
      key: 'reliance',
      label: 'Visits that searched',
      value: formatPct(kpis.searchReliancePct),
      hint: `${formatCount(kpis.sessionsWithSearch)} visits`,
      tone: searchRelianceTone(kpis.searchReliancePct),
      info: {
        what: 'The share of all visits in which at least one search was run.',
        how:
          'Search is a fallback on an intranet, so this is really a navigation measure. When most '
          + 'visits need search, the menu is not getting people where they are going - and the top '
          + 'terms below are a list of the links it should be offering.',
      },
    },
    {
      key: 'per-visit',
      label: 'Searches per searching visit',
      value: formatDecimal(kpis.searchesPerSearchingVisit),
      hint: `${formatCount(kpis.strugglingVisits)} visits searched 3+ times`,
      info: {
        what: 'How many searches a visit runs once it has started searching.',
        how:
          'Repeated searching in one visit is often someone rephrasing because the first attempt '
          + 'failed, though several unrelated searches look identical here - the count compares '
          + 'nothing about the terms. Treat visits with three or more searches as a "people may not '
          + 'be finding things" signal to investigate, not as proof of it.',
      },
    },
    {
      key: 'dead-ends',
      label: 'Searches leading nowhere',
      value: formatPct(kpis.deadEndPct),
      hint: `${formatCount(kpis.deadEndSearches)} searches`,
      tone: kpis.deadEndPct >= 50 ? 'warning' : 'neutral',
      info: {
        what: 'Searches after which the visit recorded no further page view.',
        how:
          'A proxy, not a fact. The import records the term and the time, not the result count or '
          + 'whether a result was clicked, so a genuine zero-result search and a search whose '
          + 'results were ignored look identical here. Page views within '
          + String(data.kpis.deadEndGraceSeconds)
          + ' seconds of the search do not count, because the search results page is itself a page '
          + 'view and would otherwise make every search look successful - which also means a fast '
          + 'click-through lands in this bucket, as does a search that was simply the last thing '
          + 'someone did that day. Read it as an upper bound.',
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
          title="What people search for"
          description="Size is search volume. Colour carries nothing."
          query={queryFor(data.queries, 'search-terms')}
          isEmpty={data.topTerms.length === 0}
          emptyMessage={
            searchAvailable
              ? 'No searches were recorded in this period.'
              : 'No searches have ever been recorded. The tracker captures search terms on the search results page, so it has to be deployed to the site your search centre lives on.'
          }
        >
          <WordCloud
            categories={data.topTerms.map((t) => ({ label: t.term, value: t.searches }))}
            valueLabel="Searches"
          />
        </SectionCard>

        <SectionCard
          title="Top search terms"
          description={`Top ${data.window.top} by volume, with how often each led nowhere.`}
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
              Export
            </Button>
          }
        >
          <TermTable rows={data.topTerms} label="Top search terms" />
        </SectionCard>

        <SectionCard
          title="Terms that lead nowhere"
          description={`Terms searched at least 3 times, ranked by the share of searches with no further page view.`}
          note={
            'Each row is a term that repeatedly recorded no further page view. That is worth '
            + 'checking - the page may not exist, or may not use their words - but it is not proof '
            + 'of a failed search: a visitor who clicked a result quickly, or who searched as their '
            + 'last action, counts here too.'
          }
          query={queryFor(data.queries, 'search-dead-ends')}
          isEmpty={data.deadEndTerms.length === 0}
          emptyMessage="No term was searched often enough, or failed often enough, to rank."
        >
          <TermTable rows={data.deadEndTerms} hideSearchers label="Terms that led nowhere" />
        </SectionCard>

        <SectionCard
          title="Searches over time"
          query={queryFor(data.queries, 'search-trend')}
          isEmpty={data.trend.length === 0}
        >
          <TimeSeriesChart
            valueLabel="Searches"
            series={[
              { name: 'Searches', points: data.trend.map((p) => ({ weekStart: p.weekStart, value: p.searches })) },
            ]}
          />
        </SectionCard>
      </div>

      <div className={styles.grid}>
        <SectionCard
          title="Searches by day of week"
          query={queryFor(data.queries, 'search-day-hour')}
          isEmpty={data.byDay.every((b) => b.count === 0)}
        >
          <CategoryBarChart categories={bucketsToCategories(data.byDay)} valueLabel="Searches" showShare />
        </SectionCard>

        <SectionCard
          title="Searches by period of day"
          description="UTC."
          query={queryFor(data.queries, 'search-day-hour')}
          isEmpty={data.byPeriodOfDay.every((b) => b.count === 0)}
        >
          <CategoryBarChart categories={bucketsToCategories(data.byPeriodOfDay)} valueLabel="Searches" showShare />
        </SectionCard>

        <SectionCard
          title="Where people were when they searched"
          description="The site of the last page viewed before the search."
          note={
            'Reconstructed. The import does not record where a search was launched from, so this is '
            + 'the site of the previous page view in the same visit - a search run as the very first '
            + 'action of a visit has no site and is not counted here.'
          }
          query={queryFor(data.queries, 'search-sites')}
          isEmpty={data.bySite.length === 0}
        >
          <CategoryBarChart categories={toCategories(data.bySite)} valueLabel="Searches" />
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

  return (
    <div className={styles.tableWrap}>
      <Table size="small" aria-label={label}>
        <TableHeader>
          <TableRow>
            <TableHeaderCell>Term</TableHeaderCell>
            <TableHeaderCell className={styles.numeric}>Searches</TableHeaderCell>
            {!hideSearchers && <TableHeaderCell className={styles.numeric}>People</TableHeaderCell>}
            <TableHeaderCell className={styles.numeric}>Led nowhere</TableHeaderCell>
            <TableHeaderCell className={styles.numeric}>Share</TableHeaderCell>
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

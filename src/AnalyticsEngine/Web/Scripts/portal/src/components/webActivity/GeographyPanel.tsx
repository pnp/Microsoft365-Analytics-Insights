import { Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';
import CategoryBarChart from '../charts/CategoryBarChart';
import DonutChart from '../charts/DonutChart';
import StackedAreaChart from '../charts/StackedAreaChart';
import { KpiGrid, type KpiDefinition } from '../shared/KpiGrid';
import { seriesColor } from '../charts/chartCommon';
import type { WebActivityGeography, WebActivityPlaceRow } from '../../types/webActivity';
import {
  SectionCard,
  WindowNote,
  formatCount,
  formatPct,
  queryFor,
  toStackedSeries,
  useWebActivityStyles,
} from './webActivityShared';

/**
 * The Geographics tab - the in-app replacement for the Power BI report's map page.
 *
 * Deliberately no map. The Power BI version used a Bing Maps visual, which needs a map key, sends
 * place names to a third party and - on an intranet where nearly everyone sits in three offices -
 * conveys less than a sorted list does. The question this tab actually answers is "are our remote
 * and regional offices reached at all?", and a ranked table answers it better.
 */
export default function GeographyPanel({ data }: { data: WebActivityGeography }) {
  const styles = useWebActivityStyles();
  const kpis = data.kpis;

  const items: KpiDefinition[] = [
    {
      key: 'visits',
      label: 'Visits',
      value: formatCount(kpis.visits),
      info: {
        what: 'Visits with at least one page view in the period.',
        how: 'The same visit count as the other tabs, repeated here so the shares below have a denominator on screen.',
      },
    },
    {
      key: 'visitors',
      label: 'Visitors',
      value: formatCount(kpis.visitors),
      info: { what: 'Distinct people who visited.', how: 'Distinct users behind the visiting sessions.' },
    },
    {
      key: 'countries',
      label: 'Countries',
      value: formatCount(kpis.countries),
      info: {
        what: 'Distinct countries page views came from.',
        how: 'Resolved by Application Insights from the client IP address at collection time.',
      },
    },
    {
      key: 'cities',
      label: 'Cities',
      value: formatCount(kpis.cities),
      info: {
        what: 'Distinct cities page views came from.',
        how:
          'Also IP-derived. On a corporate network this is frequently the city of the internet '
          + 'breakout rather than where the person is sitting, so read it as "which office egress '
          + 'did this come through" rather than as a home address.',
      },
    },
    {
      key: 'provinces',
      label: 'Regions',
      value: formatCount(kpis.provinces),
      info: {
        what: 'Distinct states, provinces or regions page views came from.',
        how: 'IP-derived, and populated less reliably than country.',
      },
    },
    {
      key: 'unknown',
      label: 'Unlocated views',
      value: formatPct(kpis.unknownLocationPct),
      hint: `${formatCount(kpis.unknownLocationPageViews)} page views`,
      tone: kpis.unknownLocationPct > 25 ? 'warning' : 'neutral',
      info: {
        what: 'Page views with neither a country nor a city.',
        how:
          'Shown because it is the honest denominator for everything else on this tab. A high figure '
          + 'usually means traffic arriving through a VPN or proxy that Application Insights cannot '
          + 'resolve - the location breakdown is then a sample, not a census.',
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

      <div className={styles.grid}>
        <SectionCard
          title="Countries"
          query={queryFor(data.queries, 'geo-countries')}
          isEmpty={data.countries.length === 0}
          emptyMessage="No page view in this period had a resolved country."
        >
          <DonutChart
            categories={data.countries.map((c) => ({ label: c.name, value: c.pageViews }))}
            colours={data.countries.map((_, i) => seriesColor(i))}
            centreValue={formatCount(kpis.countries)}
            centreLabel="countries"
          />
        </SectionCard>

        <SectionCard
          title="Cities"
          query={queryFor(data.queries, 'geo-cities')}
          isEmpty={data.cities.length === 0}
        >
          <PlaceTable rows={data.cities} heading="City" showCountry />
        </SectionCard>

        <SectionCard
          title="Regions"
          query={queryFor(data.queries, 'geo-provinces')}
          isEmpty={data.provinces.length === 0}
          emptyMessage="No page view in this period had a resolved state, province or region."
        >
          <PlaceTable rows={data.provinces} heading="Region" showCountry />
        </SectionCard>

        <SectionCard
          title="Country reach"
          description="Page views per country, as a share of all located traffic."
          query={queryFor(data.queries, 'geo-countries')}
          isEmpty={data.countries.length === 0}
        >
          <CategoryBarChart
            categories={data.countries.map((c) => ({ label: c.name, value: c.pageViews }))}
            valueLabel="Page views"
            showShare
          />
        </SectionCard>
      </div>

      <div className={styles.stack}>
        <SectionCard
          title="Country mix over time"
          description="Weekly page views per country, stacked - a new region appearing or an old one going quiet shows up here first."
          query={queryFor(data.queries, 'geo-country-over-time')}
          isEmpty={data.countryOverTime.length === 0}
        >
          <StackedAreaChart series={toStackedSeries(data.countryOverTime)} valueLabel="Page views" />
        </SectionCard>
      </div>
    </div>
  );
}

function PlaceTable({
  rows,
  heading,
  showCountry,
}: {
  rows: WebActivityPlaceRow[];
  heading: string;
  showCountry?: boolean;
}) {
  const styles = useWebActivityStyles();

  return (
    <div className={styles.tableWrap}>
      <Table size="small" aria-label={heading}>
        <TableHeader>
          <TableRow>
            <TableHeaderCell>{heading}</TableHeaderCell>
            {showCountry && <TableHeaderCell>Country</TableHeaderCell>}
            <TableHeaderCell className={styles.numeric}>Page views</TableHeaderCell>
            <TableHeaderCell className={styles.numeric}>Visits</TableHeaderCell>
            <TableHeaderCell className={styles.numeric}>Visitors</TableHeaderCell>
            <TableHeaderCell className={styles.numeric}>Share</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row) => (
            <TableRow key={`${row.country ?? ''}\u0000${row.name}`}>
              <TableCell className={styles.td}>{row.name}</TableCell>
              {showCountry && <TableCell className={styles.td}>{row.country ?? '\u2014'}</TableCell>}
              <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(row.pageViews)}</TableCell>
              <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(row.visits)}</TableCell>
              <TableCell className={`${styles.td} ${styles.numeric}`}>{formatCount(row.visitors)}</TableCell>
              <TableCell className={`${styles.td} ${styles.numeric}`}>{formatPct(row.sharePct)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

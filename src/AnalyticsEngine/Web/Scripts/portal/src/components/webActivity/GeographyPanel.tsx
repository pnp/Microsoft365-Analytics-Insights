import { Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';
import CategoryBarChart from '../charts/CategoryBarChart';
import DonutChart from '../charts/DonutChart';
import StackedAreaChart from '../charts/StackedAreaChart';
import { KpiGrid, type KpiDefinition } from '../shared/KpiGrid';
import { seriesColor } from '../charts/chartCommon';
import { serverPlaceholderText } from '../shared/serverPlaceholder';
import type { WebActivityGeography, WebActivityPlaceRow } from '../../types/webActivity';
import { useT } from '../../i18n';
import {
  FailedQueryNote,
  SectionCard,
  WindowNote,
  formatCount,
  formatPct,
  queryFor,
  toStackedSeries,
  useWebActivityStyles,
  withRemainder,
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
  const t = useT();
  const kpis = data.kpis;

  // The listed countries plus an explicit remainder. The denominator is the page views that
  // resolved to a COUNTRY, not the looser 'located' total - a page view with a city but no country
  // belongs to neither a country row nor this remainder, and folding it in would inflate the slice.
  const countryCategories = withRemainder(
    data.countries.map((c) => ({ label: c.name, value: c.pageViews })),
    kpis.countryPageViews,
    t('webActivity.geography.otherCountries'),
  );

  const items: KpiDefinition[] = [
    {
      key: 'visits',
      label: t('webActivity.common.visits'),
      value: formatCount(kpis.visits),
      info: {
        what: t('webActivity.geography.kpi.visitsWhat'),
        how: t('webActivity.geography.kpi.visitsHow'),
      },
    },
    {
      key: 'visitors',
      label: t('webActivity.common.visitors'),
      value: formatCount(kpis.visitors),
      info: { what: t('webActivity.geography.kpi.visitorsWhat'), how: t('webActivity.geography.kpi.visitorsHow') },
    },
    {
      key: 'countries',
      label: t('webActivity.geography.countries'),
      value: formatCount(kpis.countries),
      info: {
        what: t('webActivity.geography.kpi.countriesWhat'),
        how: t('webActivity.geography.kpi.countriesHow'),
      },
    },
    {
      key: 'cities',
      label: t('webActivity.geography.cities'),
      value: formatCount(kpis.cities),
      info: {
        what: t('webActivity.geography.kpi.citiesWhat'),
        how: t('webActivity.geography.kpi.citiesHow'),
      },
    },
    {
      key: 'provinces',
      label: t('webActivity.geography.regions'),
      value: formatCount(kpis.provinces),
      info: {
        what: t('webActivity.geography.kpi.regionsWhat'),
        how: t('webActivity.geography.kpi.regionsHow'),
      },
    },
    {
      key: 'unknown',
      label: t('webActivity.geography.kpi.unlocatedViews'),
      value: formatPct(kpis.unknownLocationPct),
      hint: t('webActivity.common.pageViewsCount', { count: formatCount(kpis.unknownLocationPageViews) }),
      tone: kpis.unknownLocationPct > 25 ? 'warning' : 'neutral',
      info: {
        what: t('webActivity.geography.kpi.unlocatedViewsWhat'),
        how: t('webActivity.geography.kpi.unlocatedViewsHow'),
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

      <div className={styles.grid}>
        <SectionCard
          title={t('webActivity.geography.countries')}
          query={queryFor(data.queries, 'geo-countries')}
          isEmpty={data.countries.length === 0}
          emptyMessage={t('webActivity.geography.countriesEmpty')}
        >
          <DonutChart
            categories={countryCategories}
            colours={countryCategories.map((_, i) => seriesColor(i))}
            centreValue={formatCount(kpis.countries)}
            centreLabel={t('webActivity.geography.countriesLower')}
          />
        </SectionCard>

        <SectionCard
          title={t('webActivity.geography.cities')}
          query={queryFor(data.queries, 'geo-cities')}
          isEmpty={data.cities.length === 0}
        >
          <PlaceTable rows={data.cities} heading={t('webActivity.geography.city')} showCountry />
        </SectionCard>

        <SectionCard
          title={t('webActivity.geography.regions')}
          query={queryFor(data.queries, 'geo-provinces')}
          isEmpty={data.provinces.length === 0}
          emptyMessage={t('webActivity.geography.regionsEmpty')}
        >
          <PlaceTable rows={data.provinces} heading={t('webActivity.geography.region')} showCountry />
        </SectionCard>

        <SectionCard
          title={t('webActivity.geography.countryReach.title')}
          description={t('webActivity.geography.countryReach.description')}
          query={queryFor(data.queries, 'geo-countries')}
          isEmpty={data.countries.length === 0}
        >
          <CategoryBarChart categories={countryCategories} valueLabel={t('webActivity.common.pageViews')} showShare />
        </SectionCard>
      </div>

      <div className={styles.stack}>
        <SectionCard
          title={t('webActivity.geography.countryMix.title')}
          description={t('webActivity.geography.countryMix.description')}
          query={queryFor(data.queries, 'geo-country-over-time')}
          isEmpty={data.countryOverTime.length === 0}
        >
          <StackedAreaChart series={toStackedSeries(data.countryOverTime)} valueLabel={t('webActivity.common.pageViews')} />
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
  const t = useT();

  return (
    <div className={styles.tableWrap}>
      <Table size="small" aria-label={heading}>
        <TableHeader>
          <TableRow>
            <TableHeaderCell>{heading}</TableHeaderCell>
            {showCountry && <TableHeaderCell>{t('webActivity.geography.country')}</TableHeaderCell>}
            <TableHeaderCell className={styles.numeric}>{t('webActivity.common.pageViews')}</TableHeaderCell>
            <TableHeaderCell className={styles.numeric}>{t('webActivity.common.visits')}</TableHeaderCell>
            <TableHeaderCell className={styles.numeric}>{t('webActivity.common.visitors')}</TableHeaderCell>
            <TableHeaderCell className={styles.numeric}>{t('webActivity.common.share')}</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row) => (
            <TableRow key={`${row.country ?? ''}\u0000${row.name}`}>
              <TableCell className={styles.td}>{serverPlaceholderText(t, row.name)}</TableCell>
              {showCountry && <TableCell className={styles.td}>{serverPlaceholderText(t, row.country) ?? '\u2014'}</TableCell>}
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

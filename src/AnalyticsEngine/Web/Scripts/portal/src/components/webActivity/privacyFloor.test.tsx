import { describe, expect, it } from 'vitest';
import { screen, within } from '@testing-library/react';

import { loadCatalog } from '../../i18n';
import { renderWithProvider } from '../../test/renderWithProvider';
import GeographyPanel from './GeographyPanel';
import JourneysPanel from './JourneysPanel';
import OverviewPanel from './OverviewPanel';
import PagesPanel from './PagesPanel';
import SearchPanel from './SearchPanel';
import type {
  WebActivityGeography,
  WebActivityJourneys,
  WebActivityOverview,
  WebActivityPageRow,
  WebActivityPages,
  WebActivitySearch,
  WebActivityWindow,
} from '../../types/webActivity';

// What a reader without See PII is sent (#844): search terms withheld, and rows of too few people left out of every other
// list. Every card must say so, in the reader's language, rather than read as "nothing happened" - and a list that lost
// every row, or is withheld, offers no export.

const window: WebActivityWindow = {
  days: 28,
  fromUtc: '2026-08-13T00:00:00Z',
  toUtc: '2026-09-10T00:00:00Z',
  workingDays: 20,
  top: 15,
  minimumViews: 5,
  segmentsFullyReachable: true,
  minimumPagesForDecile: 10,
};

const noop = () => undefined;

function search(overrides: Partial<WebActivitySearch> = {}): WebActivitySearch {
  return {
    window,
    queries: [],
    kpis: {
      searches: 17,
      terms: 3,
      searchers: 10,
      sessionsWithSearch: 12,
      searchReliancePct: 4,
      searchesPerSearchingVisit: 1.4,
      strugglingVisits: 1,
      deadEndSearches: 3,
      deadEndPct: 17.6,
      deadEndGraceSeconds: 5,
    },
    topTerms: [],
    deadEndTerms: [],
    trend: [],
    byDay: [],
    byPeriodOfDay: [],
    bySite: [],
    minimumGroupSize: 5,
    termsWithheld: true,
    sitesWithheld: true,
    ...overrides,
  };
}

function geography(overrides: Partial<WebActivityGeography> = {}): WebActivityGeography {
  return {
    window,
    queries: [],
    kpis: {
      visits: 300,
      visitors: 60,
      countries: 3,
      cities: 4,
      provinces: 3,
      unknownLocationPageViews: 0,
      unknownLocationPct: 0,
      locatedPageViews: 529,
      countryPageViews: 529,
      cityPageViews: 404,
      provincePageViews: 475,
    },
    countries: [{ name: 'United Kingdom', country: null, pageViews: 400, visits: 120, visitors: 40, sharePct: 75.6 }],
    cities: [{ name: 'London', country: 'United Kingdom', pageViews: 300, visits: 90, visitors: 30, sharePct: 74.3 }],
    provinces: [{ name: 'England', country: 'United Kingdom', pageViews: 380, visits: 110, visitors: 35, sharePct: 80 }],
    countryOverTime: [],
    minimumGroupSize: 5,
    withheldCountries: 2,
    withheldCities: 3,
    withheldProvinces: 1,
    ...overrides,
  };
}

const page = (title: string, pageViews: number): WebActivityPageRow => ({
  title,
  url: `https://contoso.sharepoint.com/sites/intranet/SitePages/${title}.aspx`,
  site: 'Contoso HR',
  pageViews,
  uniquePageViews: pageViews,
  visitors: 0,
  averageSecondsOnPage: 30,
  averageLoadSeconds: 1.2,
  entries: pageViews,
  exits: pageViews,
  bounces: 0,
  bouncePct: 0,
});

function journeys(overrides: Partial<WebActivityJourneys> = {}): WebActivityJourneys {
  return {
    window,
    queries: [],
    kpis: {
      visits: 300,
      bounces: 100,
      bouncePct: 33.3,
      pagesPerVisit: 2,
      medianPagesPerVisit: 2,
      averageVisitSeconds: 60,
      measuredVisits: 200,
      clicks: 12,
    },
    entryPages: [page('Home', 300)],
    exitPages: [page('Home', 120)],
    bouncePages: [],
    transitions: [
      {
        fromTitle: 'Home',
        fromUrl: 'https://contoso.sharepoint.com/sites/intranet/SitePages/Home.aspx',
        toTitle: 'News',
        toUrl: 'https://contoso.sharepoint.com/sites/intranet/SitePages/News.aspx',
        count: 60,
        sharePct: 60,
      },
    ],
    flows: [],
    flowsCoveragePct: 0,
    depth: [],
    clickedElements: [],
    minimumGroupSize: 5,
    withheldEntryPages: 2,
    withheldExitPages: 2,
    withheldBouncePages: 2,
    withheldTransitions: 2,
    withheldFlows: 3,
    withheldClickedElements: 1,
    ...overrides,
  };
}

function pages(overrides: Partial<WebActivityPages> = {}): WebActivityPages {
  return {
    window,
    queries: [],
    kpis: {
      pageViews: 1000,
      uniquePageViews: 600,
      uniqueSharePct: 60,
      pagesPerVisit: 2,
      averageSecondsOnPage: 40,
      averageLoadSeconds: 1.2,
      uniquePages: 40,
      quietPages: 12,
      topDecilePagePct: 55,
    },
    topPages: [page('Home', 500), page('News', 200)],
    slowestPages: [page('Expenses', 40)],
    quietPages: [],
    bySite: [
      {
        name: 'Contoso Intranet',
        url: 'https://contoso.sharepoint.com/sites/intranet',
        pageViews: 800,
        visitPageViews: 780,
        uniquePageViews: 500,
        visits: 250,
        visitors: 50,
      },
    ],
    periodOverTime: [],
    minimumGroupSize: 5,
    withheldSites: 2,
    withheldTopPages: 2,
    withheldSlowestPages: 2,
    withheldQuietPages: 12,
    visitFiguresWithheld: true,
    ...overrides,
  };
}

function overview(overrides: Partial<WebActivityOverview> = {}): WebActivityOverview {
  return {
    window,
    queries: [],
    kpis: {
      pageViews: 1000,
      uniquePageViews: 600,
      visits: 300,
      visitors: 53,
      knownUsers: 200,
      reachPct: 26.5,
      directoryImported: true,
      uniquePages: 40,
      sites: 3,
      pagesPerVisit: 3.3,
      bouncePct: 30,
      averageSecondsOnPage: 40,
      averageLoadSeconds: 1.2,
      newVisitors: 5,
      returningVisitors: 40,
      mobilePageViewPct: 10,
    },
    trend: [],
    // Three occasional visitors were left out, with the rare ones, the smallest segment shown, beside them.
    visitorSegments: [
      { key: 'Daily', label: 'Daily', count: 30, sharePct: 56.6 },
      { key: 'Regular', label: 'Regular', count: 12, sharePct: 22.6 },
    ],
    visitDepth: [],
    heatmap: [],
    topSites: [{ name: 'Contoso Intranet', count: 800, sharePct: 80 }],
    judgements: [],
    minimumGroupSize: 5,
    withheldSites: 0,
    withheldSegments: 2,
    ...overrides,
  };
}

const SEARCH_TERMS_WITHHELD_EN = /^Hidden to protect privacy: search terms are text people typed/;
const NO_PEOPLE_COUNT_EN = /^Hidden to protect privacy: nothing recorded says how many people are behind each row/;
const TWO_LEFT_OUT_EN = /\b2 rows are left out to protect privacy, so that no group of fewer than 5 people can be seen or worked out/;
const ONE_LEFT_OUT_EN = 'One row is left out to protect privacy, so that no group of fewer than 5 people can be seen or worked out from the totals.';
const ONE_LEFT_OUT_ES =
  'Se omite una fila para proteger la privacidad, de modo que ningún grupo de menos de 5 personas pueda verse ni deducirse de los totales.';

describe('Web activity for a reader without See PII', () => {
  it('says search terms and searches by site are withheld, and offers no export of them', () => {
    renderWithProvider(<SearchPanel data={search()} searchAvailable onExportTerms={noop} exporting={false} />);

    // The word cloud, the term table and the dead-end table all say why they are empty.
    expect(screen.getAllByText(SEARCH_TERMS_WITHHELD_EN)).toHaveLength(3);
    expect(screen.getByText(NO_PEOPLE_COUNT_EN)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Export' })).not.toBeInTheDocument();
  });

  it('says so in Spanish', async () => {
    await loadCatalog('es');
    renderWithProvider(<SearchPanel data={search()} searchAvailable onExportTerms={noop} exporting={false} />, { language: 'es' });

    expect(
      await screen.findAllByText(/^Oculto para proteger la privacidad: los términos de búsqueda son texto/, undefined, { timeout: 5000 }),
    ).toHaveLength(3);
    expect(screen.getByText(/^Oculto para proteger la privacidad: nada de lo registrado indica cuántas personas/)).toBeInTheDocument();
    expect(screen.queryByText(SEARCH_TERMS_WITHHELD_EN)).not.toBeInTheDocument();
  });

  it('says how many places were left out of each list, with the minimum, in English and Spanish', async () => {
    const { unmount } = renderWithProvider(<GeographyPanel data={geography()} />);

    // The countries note is on the countries ring, the reach chart and the countries-over-time chart.
    expect(
      screen.getAllByText(
        '2 rows are left out to protect privacy, so that no group of fewer than 5 people can be seen or worked out from the totals.',
      ),
    ).toHaveLength(3);
    expect(screen.getByText(/^3 rows are left out to protect privacy, so that no group of fewer than 5 people/)).toBeInTheDocument();
    expect(screen.getByText(ONE_LEFT_OUT_EN)).toBeInTheDocument();
    unmount();

    await loadCatalog('es');
    renderWithProvider(<GeographyPanel data={geography({ withheldCities: 12345 })} />, { language: 'es' });
    expect(
      await screen.findByText(/^Se omiten 12\.345 filas para proteger la privacidad, de modo que ningún grupo de menos de 5 personas/, undefined, {
        timeout: 5000,
      }),
    ).toBeInTheDocument();
    expect(screen.getByText(ONE_LEFT_OUT_ES)).toBeInTheDocument();
  });

  it('shows the floored journeys with how many rows were left out, and says so in place of a list that lost every row', () => {
    renderWithProvider(
      <JourneysPanel
        data={journeys()}
        clickTrackingAvailable
        onExportEntry={noop}
        onExportExit={noop}
        onExportTransitions={noop}
        onExportFlows={noop}
        exporting={false}
      />,
    );

    // Entry and exit pages, the bounce pages (whose every row was left out) and the routes.
    expect(screen.getAllByText(TWO_LEFT_OUT_EN)).toHaveLength(4);
    expect(screen.getByText(/^3 rows are left out to protect privacy/)).toBeInTheDocument();
    expect(screen.getByText(ONE_LEFT_OUT_EN)).toBeInTheDocument();
    expect(screen.queryByText(NO_PEOPLE_COUNT_EN)).not.toBeInTheDocument();
    // A list that lost every row does not claim the period had none.
    expect(screen.queryByText('No entry page had enough entries to rank by bounce rate.')).not.toBeInTheDocument();
    expect(screen.queryByText('No visit in this period could be paired to a start and an end page.')).not.toBeInTheDocument();
    expect(screen.queryByText('No element clicks were recorded in this period.')).not.toBeInTheDocument();
    expect(screen.getAllByText('Home').length).toBeGreaterThan(0);
    // Entry pages, exit pages and routes can be exported; the flows, every one of them left out, cannot.
    expect(screen.getAllByRole('button', { name: 'Export' })).toHaveLength(3);
  });

  it('shows the floored page lists with how many pages were left out, and every quiet page left out', () => {
    renderWithProvider(
      <PagesPanel data={pages()} onExportPages={noop} onExportQuiet={noop} onExportSlow={noop} exporting={false} />,
    );

    // Most viewed, slowest and the sites.
    expect(screen.getAllByText(TWO_LEFT_OUT_EN)).toHaveLength(3);
    expect(screen.getByText(/^12 rows are left out to protect privacy/)).toBeInTheDocument();
    expect(screen.queryByText('Every page that was viewed at all was viewed more than three times.')).not.toBeInTheDocument();
    expect(screen.queryByText(NO_PEOPLE_COUNT_EN)).not.toBeInTheDocument();
    expect(screen.getByText('Expenses')).toBeInTheDocument();
    expect(screen.getByText('Contoso Intranet')).toBeInTheDocument();
    // Most viewed and slowest can be exported; the quiet pages, every one left out, cannot.
    expect(screen.getAllByRole('button', { name: 'Export' })).toHaveLength(2);
    // No page carries a figure counted in visits here: the tables drop those columns, and the most viewed table says why.
    expect(
      screen.getByText(/^Unique page views, entries, exits and bounces are left out of this table to protect privacy\. The Visits tab shows each page's visits/),
    ).toBeInTheDocument();
    const mostViewed = within(screen.getByRole('table', { name: 'Most viewed pages' }));
    expect(mostViewed.getByRole('columnheader', { name: 'Page views' })).toBeInTheDocument();
    expect(mostViewed.queryByRole('columnheader', { name: 'Unique' })).not.toBeInTheDocument();
    expect(mostViewed.queryByRole('columnheader', { name: 'Entries' })).not.toBeInTheDocument();
    expect(mostViewed.queryByRole('columnheader', { name: 'Bounce' })).not.toBeInTheDocument();
  });

  it('says the pages and journeys left out in Spanish', async () => {
    await loadCatalog('es');
    const { unmount } = renderWithProvider(
      <PagesPanel data={pages({ withheldQuietPages: 12345 })} onExportPages={noop} onExportQuiet={noop} onExportSlow={noop} exporting={false} />,
      { language: 'es' },
    );

    expect(
      await screen.findByText(/^Se omiten 12\.345 filas para proteger la privacidad, de modo que ningún grupo de menos de 5 personas/, undefined, {
        timeout: 5000,
      }),
    ).toBeInTheDocument();
    expect(screen.getAllByText(/Se omiten 2 filas para proteger la privacidad/)).toHaveLength(3);
    expect(screen.queryByText('Todas las páginas que se vieron al menos una vez se vieron más de tres veces.')).not.toBeInTheDocument();
    expect(screen.queryByText(TWO_LEFT_OUT_EN)).not.toBeInTheDocument();
    expect(
      screen.getByText(
        /^Las páginas vistas únicas, las entradas, las salidas y los rebotes se omiten de esta tabla para proteger la privacidad\. La pestaña Visitas muestra las visitas de cada página/,
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText(/^Unique page views, entries, exits and bounces are left out/)).not.toBeInTheDocument();
    unmount();

    renderWithProvider(
      <JourneysPanel
        data={journeys()}
        clickTrackingAvailable
        onExportEntry={noop}
        onExportExit={noop}
        onExportTransitions={noop}
        onExportFlows={noop}
        exporting={false}
      />,
      { language: 'es' },
    );
    expect(
      await screen.findByText(ONE_LEFT_OUT_ES, undefined, { timeout: 5000 }),
    ).toBeInTheDocument();
    expect(screen.getByText(/^Se omiten 3 filas para proteger la privacidad/)).toBeInTheDocument();
    expect(screen.queryByText('No se pudo emparejar ninguna visita de este periodo con una página de inicio y otra de fin.')).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Exportar' })).toHaveLength(3);
  });

  it('draws the engagement segments left out as one slice of the visitors, and says how many, in English and Spanish', async () => {
    const { unmount } = renderWithProvider(<OverviewPanel data={overview()} directoryImported />);

    expect(screen.getByText(TWO_LEFT_OUT_EN)).toBeInTheDocument();
    // The ring is still all 53 visitors: the 11 in the segments left out are one slice, not shared among the others.
    const slice = screen.getByText('Left out to protect privacy');
    expect(slice.nextElementSibling).toHaveTextContent('11 (20.8%)');
    unmount();

    await loadCatalog('es');
    renderWithProvider(<OverviewPanel data={overview()} directoryImported />, { language: 'es' });
    expect(await screen.findByText('Omitido para proteger la privacidad', undefined, { timeout: 5000 })).toBeInTheDocument();
    expect(screen.getByText(/Se omiten 2 filas para proteger la privacidad/)).toBeInTheDocument();
    expect(screen.queryByText('Left out to protect privacy')).not.toBeInTheDocument();
    expect(screen.queryByText(TWO_LEFT_OUT_EN)).not.toBeInTheDocument();
  });
});

describe('Web activity for a reader with See PII', () => {
  it('shows no privacy note and keeps every export', () => {
    renderWithProvider(
      <PagesPanel
        data={pages({
          minimumGroupSize: null,
          withheldSites: 0,
          withheldTopPages: 0,
          withheldSlowestPages: 0,
          withheldQuietPages: 0,
          visitFiguresWithheld: undefined,
        })}
        onExportPages={noop}
        onExportQuiet={noop}
        onExportSlow={noop}
        exporting={false}
      />,
    );

    expect(screen.queryByText(NO_PEOPLE_COUNT_EN)).not.toBeInTheDocument();
    expect(screen.queryByText(/left out to protect privacy/)).not.toBeInTheDocument();
    expect(screen.queryByText(/^Unique page views, entries, exits and bounces are left out/)).not.toBeInTheDocument();
    const mostViewed = within(screen.getByRole('table', { name: 'Most viewed pages' }));
    expect(mostViewed.getByRole('columnheader', { name: 'Unique' })).toBeInTheDocument();
    expect(mostViewed.getByRole('columnheader', { name: 'Entries' })).toBeInTheDocument();
    // An empty quiet list says why, as it always did.
    expect(screen.getByText('Every page that was viewed at all was viewed more than three times.')).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Export' })).toHaveLength(3);
  });

  it('draws every engagement segment with no slice left out', () => {
    renderWithProvider(
      <OverviewPanel
        data={overview({
          minimumGroupSize: null,
          withheldSegments: 0,
          visitorSegments: [
            { key: 'Daily', label: 'Daily', count: 30, sharePct: 56.6 },
            { key: 'Regular', label: 'Regular', count: 12, sharePct: 22.6 },
            { key: 'Occasional', label: 'Occasional', count: 3, sharePct: 5.7 },
            { key: 'Rare', label: 'Rare', count: 8, sharePct: 15.1 },
          ],
        })}
        directoryImported
      />,
    );

    expect(screen.queryByText('Left out to protect privacy')).not.toBeInTheDocument();
    expect(screen.queryByText(/left out to protect privacy/)).not.toBeInTheDocument();
  });

  it('shows no note on a section sent before the floor existed', () => {
    const older = geography();
    delete older.minimumGroupSize;
    delete older.withheldCountries;
    delete older.withheldCities;
    delete older.withheldProvinces;
    renderWithProvider(<GeographyPanel data={older} />);

    expect(screen.queryByText(/left out to protect privacy/)).not.toBeInTheDocument();
  });
});

import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';

import { loadCatalog } from '../../i18n';
import { renderWithProvider } from '../../test/renderWithProvider';
import GeographyPanel from './GeographyPanel';
import JourneysPanel from './JourneysPanel';
import PagesPanel from './PagesPanel';
import SearchPanel from './SearchPanel';
import type {
  WebActivityGeography,
  WebActivityJourneys,
  WebActivityPages,
  WebActivitySearch,
  WebActivityWindow,
} from '../../types/webActivity';

// What a reader without See PII is sent (#844): lists withheld, and rows of too few people left out. Every card must say
// so, in the reader's language, rather than read as "nothing happened" - and a withheld list offers no export.

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
    entryPages: [],
    exitPages: [],
    bouncePages: [],
    transitions: [],
    flows: [],
    flowsCoveragePct: 0,
    depth: [],
    clickedElements: [],
    minimumGroupSize: 5,
    pageListsWithheld: true,
    pathsWithheld: true,
    clickedElementsWithheld: true,
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
    topPages: [],
    slowestPages: [],
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
    pageListsWithheld: true,
    ...overrides,
  };
}

const SEARCH_TERMS_WITHHELD_EN = /^Hidden to protect privacy: search terms are text people typed/;
const NO_PEOPLE_COUNT_EN = /^Hidden to protect privacy: nothing recorded says how many people are behind each row/;

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
        '2 rows are left out to protect privacy: those with fewer than 5 people behind them, together with enough of the smallest other rows that none can be worked out from the totals.',
      ),
    ).toHaveLength(3);
    expect(screen.getByText(/^3 rows are left out to protect privacy: those with fewer than 5 people/)).toBeInTheDocument();
    expect(screen.getByText('One row is left out to protect privacy: fewer than 5 people are behind it.')).toBeInTheDocument();
    unmount();

    await loadCatalog('es');
    renderWithProvider(<GeographyPanel data={geography({ withheldCities: 12345 })} />, { language: 'es' });
    expect(
      await screen.findByText(/^Se omiten 12\.345 filas para proteger la privacidad: las que tienen menos de 5 personas detrás/, undefined, {
        timeout: 5000,
      }),
    ).toBeInTheDocument();
    expect(screen.getByText('Se omite una fila para proteger la privacidad: hay menos de 5 personas detrás de ella.')).toBeInTheDocument();
  });

  it('says the page lists, paths and clicked elements are withheld, and offers no export of them', () => {
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

    // Entry, bounce and exit pages, the flows, the routes and the clicked elements.
    expect(screen.getAllByText(NO_PEOPLE_COUNT_EN)).toHaveLength(6);
    expect(screen.queryByRole('button', { name: 'Export' })).not.toBeInTheDocument();
  });

  it('withholds the page lists and says how many sites were left out', () => {
    renderWithProvider(
      <PagesPanel data={pages()} onExportPages={noop} onExportQuiet={noop} onExportSlow={noop} exporting={false} />,
    );

    expect(screen.getAllByText(NO_PEOPLE_COUNT_EN)).toHaveLength(3);
    expect(screen.getByText(/^2 rows are left out to protect privacy/)).toBeInTheDocument();
    expect(screen.getByText('Contoso Intranet')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Export' })).not.toBeInTheDocument();
  });
});

describe('Web activity for a reader with See PII', () => {
  it('shows no privacy note and keeps every export', () => {
    renderWithProvider(
      <PagesPanel
        data={pages({ minimumGroupSize: null, withheldSites: 0, pageListsWithheld: false })}
        onExportPages={noop}
        onExportQuiet={noop}
        onExportSlow={noop}
        exporting={false}
      />,
    );

    expect(screen.queryByText(NO_PEOPLE_COUNT_EN)).not.toBeInTheDocument();
    expect(screen.queryByText(/left out to protect privacy/)).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Export' })).toHaveLength(3);
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

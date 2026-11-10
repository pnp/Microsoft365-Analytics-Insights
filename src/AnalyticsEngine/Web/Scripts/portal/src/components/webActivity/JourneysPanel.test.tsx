import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';

import { renderWithProvider } from '../../test/renderWithProvider';
import JourneysPanel from './JourneysPanel';
import type { WebActivityJourneyKpis, WebActivityJourneys } from '../../types/webActivity';

function journeys(kpis: Partial<WebActivityJourneyKpis>): WebActivityJourneys {
  return {
    window: {
      days: 28,
      fromUtc: '2026-08-13T00:00:00Z',
      toUtc: '2026-09-10T00:00:00Z',
      workingDays: 20,
      top: 25,
      minimumViews: 1,
      segmentsFullyReachable: true,
      minimumPagesForDecile: 10,
    },
    queries: [],
    kpis: {
      visits: 4,
      bounces: 1,
      bouncePct: 25,
      pagesPerVisit: 1.75,
      medianPagesPerVisit: 2,
      averageVisitSeconds: 75,
      measuredVisits: 2,
      clicks: 0,
      ...kpis,
    },
    entryPages: [],
    exitPages: [],
    bouncePages: [],
    transitions: [],
    flows: [],
    flowsCoveragePct: 0,
    depth: [],
    clickedElements: [],
  };
}

function renderPanel(data: WebActivityJourneys) {
  const noop = () => undefined;
  return renderWithProvider(
    <JourneysPanel
      data={data}
      clickTrackingAvailable={false}
      onExportEntry={noop}
      onExportExit={noop}
      onExportTransitions={noop}
      onExportFlows={noop}
      exporting={false}
    />,
  );
}

describe('JourneysPanel average visit length', () => {
  it('says how many of the visits the average is over', () => {
    renderPanel(journeys({}));

    expect(screen.getByText('over the 2 of 4 visits with a measured time')).toBeTruthy();
  });

  it('says that no visit was measured, rather than leaving a bare dash', () => {
    renderPanel(journeys({ averageVisitSeconds: null, measuredVisits: 0 }));

    expect(screen.getByText('no visit in this period reported a time on page')).toBeTruthy();
  });
});

import { describe, it, expect } from 'vitest';
import { screen, fireEvent } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import DataSourceSummary from './DataSourceSummary';
import type { LicenceActivityCoverage } from '../../types/licenceActivity';
import { loadCatalog, translateStatic } from '../../i18n';
import { coverageMessage, measureLabel } from './sources';
import { demographicName } from './DemographicBreakdown';

function cov(over: Partial<LicenceActivityCoverage> = {}): LicenceActivityCoverage {
  return {
    workload: 'teams',
    status: 'available',
    source: 'microsoftGraphUsageReport',
    measure: 'activity counted by Microsoft',
    measureKey: null,
    granularity: 'weeklySupportingSnapshot',
    message: null,
    messageKey: null,
    effectiveFromUtc: '2026-04-22T00:00:00Z',
    effectiveToUtc: '2026-05-19T00:00:00Z',
    latestImportUtc: '2026-05-20T00:00:00Z',
    lagDays: 1,
    reportPeriodDays: 28,
    expectedSamples: 100,
    observedSamples: 95,
    unmatchedUsers: 0,
    snapshotDates: [],
    ...over,
  };
}

const NOW = new Date('2026-05-22T12:00:00Z');

const FIVE: LicenceActivityCoverage[] = [
  cov({ workload: 'teams', status: 'available' }),
  cov({ workload: 'outlook', status: 'available' }),
  cov({ workload: 'onedrive', status: 'available' }),
  cov({ workload: 'sharepoint', status: 'partial' }),
  cov({ workload: 'copilot', status: 'notImported' }),
];

describe('DataSourceSummary', () => {
  it('is collapsed by default: a status summary is shown, the full detail is not', () => {
    renderWithProvider(
      <DataSourceSummary coverage={FIVE} generatedUtc="2026-05-20T10:00:00Z" expiresUtc="2026-05-20T10:05:00Z" now={NOW} />,
    );

    // The at-a-glance chip row summarises the services by status.
    expect(screen.getByText(/3 Available/)).toBeInTheDocument();
    expect(screen.getByText(/1 Partial/)).toBeInTheDocument();
    expect(screen.getByText(/1 Not imported/)).toBeInTheDocument();
    expect(screen.getByText(/prepared/i)).toBeInTheDocument();

    // The full CoveragePanel (its "held for up to ..." caption is unique to it) stays hidden until asked for.
    expect(screen.queryByText(/held for up to/i)).not.toBeInTheDocument();

    const toggle = screen.getByRole('button', { name: /show data sources/i });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });

  it('expands to reveal the full per-service provenance, then collapses again', () => {
    renderWithProvider(
      <DataSourceSummary coverage={FIVE} generatedUtc="2026-05-20T10:00:00Z" expiresUtc="2026-05-20T10:05:00Z" now={NOW} />,
    );

    fireEvent.click(screen.getByRole('button', { name: /show data sources/i }));

    // The full panel is now present: its freshness caption and a translated source line appear.
    expect(screen.getByText(/held for up to/i)).toBeInTheDocument();
    expect(screen.getAllByText(/Microsoft 365 usage reports/).length).toBeGreaterThan(0);

    const toggle = screen.getByRole('button', { name: /hide data sources/i });
    expect(toggle).toHaveAttribute('aria-expanded', 'true');

    fireEvent.click(toggle);
    expect(screen.queryByText(/held for up to/i)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /show data sources/i })).toBeInTheDocument();
  });

  it('handles an empty coverage list without crashing', () => {
    renderWithProvider(
      <DataSourceSummary coverage={[]} generatedUtc="2026-05-20T10:00:00Z" expiresUtc="2026-05-20T10:05:00Z" now={NOW} />,
    );
    expect(screen.getByText(/No source information was reported/i)).toBeInTheDocument();
    // Nothing to expand into, but the control is still present and inert-safe.
    expect(screen.getByRole('button', { name: /show data sources/i })).toBeInTheDocument();
  });

  it('translates coverage measure and message keys, with server English fallback', async () => {
    await loadCatalog('es');
    const es = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
      translateStatic('es', key, values);

    expect(measureLabel('copilot.microsoftReportPrompts', 'Copilot prompts counted by Microsoft, averaged across the readings', es))
      .toBe('indicaciones de Copilot contadas por Microsoft, promediadas entre las lecturas');
    expect(coverageMessage('m365.notImported', 'Collection is switched on for this service, but no report has arrived yet.', es))
      .toBe('La recopilación está activada para este servicio, pero aún no ha llegado ningún informe.');
    expect(measureLabel(null, 'New server measure', es)).toBe('New server measure');
    expect(coverageMessage(null, 'New server message', es)).toBe('New server message');
  });

  it('translates the Copilot fallback row the SQL writes from C#, and leaves English as the server wrote it', async () => {
    await loadCatalog('es');
    const es = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
      translateStatic('es', key, values);
    const en = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
      translateStatic('en', key, values);
    const disabled = 'Copilot collection is switched off on this deployment, so nothing can be measured. That is not the same as nobody using Copilot.';
    const notImported = 'Collection is switched on for Copilot, but no Copilot activity has arrived for these dates yet.';

    expect(coverageMessage('copilot.disabled', disabled, es)).toBe(
      'La recopilación de Copilot está desactivada en esta implementación, por lo que no se puede medir nada. Eso no significa que nadie use Copilot.');
    expect(coverageMessage('copilot.notImported', notImported, es)).toBe(
      'La recopilación está activada para Copilot, pero aún no ha llegado ninguna actividad de Copilot para estas fechas.');
    expect(coverageMessage('copilot.disabled', disabled, en)).toBe(disabled);
    expect(coverageMessage('copilot.notImported', notImported, en)).toBe(notImported);
  });

  it('translates only the id 0 demographic bucket, not a real group named Unknown', async () => {
    await loadCatalog('es');
    const es = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
      translateStatic('es', key, values);

    expect(demographicName(es, { id: 0, name: 'Unknown' })).toBe('Desconocido');
    expect(demographicName(es, { id: 42, name: 'Unknown' })).toBe('Unknown');
  });

  it('shows English readers the same "Unknown" the server names the id 0 bucket, as the export does', () => {
    const en = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
      translateStatic('en', key, values);

    expect(demographicName(en, { id: 0, name: 'Unknown' })).toBe('Unknown');
    expect(demographicName(en, { id: 7, name: 'Research' })).toBe('Research');
  });
});

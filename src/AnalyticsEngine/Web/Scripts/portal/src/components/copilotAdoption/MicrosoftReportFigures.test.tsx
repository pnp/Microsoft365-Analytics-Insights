import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import { loadCatalog } from '../../i18n';
import MicrosoftReportFigures from './MicrosoftReportFigures';
import type { CopilotAdoptionSummary } from '../../types/copilotAdoption';

/** Only the fields the line reads. */
function summaryWith(over: Partial<CopilotAdoptionSummary> = {}): CopilotAdoptionSummary {
  return {
    unscopedSections: [],
    microsoftReportDate: '2026-10-05T00:00:00Z',
    microsoftReportPeriodDays: 28,
    microsoftReportVersion: 'v2',
    microsoftReportPromptsSubmitted: 12345,
    microsoftReportAveragePromptsPerActiveUser: 18.2,
    ...over,
  } as CopilotAdoptionSummary;
}

describe('MicrosoftReportFigures', () => {
  it('names Microsoft’s report as the source and states its period and report date beside the figures', () => {
    renderWithProvider(<MicrosoftReportFigures summary={summaryWith()} />);

    expect(screen.getByText('Microsoft\'s Copilot usage report')).toBeVisible();
    expect(screen.getByText('28-day period to 5 Oct 2026')).toBeVisible();
    expect(screen.getByText('12,345')).toBeVisible();
    expect(screen.getByText('prompts submitted')).toBeVisible();
    expect(screen.getByText('18.2')).toBeVisible();
    expect(screen.getByText('prompts per active user')).toBeVisible();
  });

  it('carries its caveats on the surface, not only behind the info button', () => {
    renderWithProvider(<MicrosoftReportFigures summary={summaryWith()} />);

    const caveat = screen.getByText(/licensed users only/);
    expect(caveat).toBeVisible();
    expect(caveat.textContent).toContain('rather than agents alone');
    expect(caveat.textContent).toContain('Never added to this page\'s audit-log figures');
  });

  it('states a 30-day version 1 period as 30 days, and shows a dash and the reason rather than a zero', () => {
    renderWithProvider(
      <MicrosoftReportFigures
        summary={summaryWith({
          microsoftReportPeriodDays: 30,
          microsoftReportVersion: 'v1',
          microsoftReportPromptsSubmitted: null,
          microsoftReportAveragePromptsPerActiveUser: null,
        })}
      />,
    );

    expect(screen.getByText('30-day period to 5 Oct 2026')).toBeVisible();
    expect(screen.getAllByText('\u2014')).toHaveLength(2);
    expect(screen.queryByText('0')).not.toBeInTheDocument();
    expect(screen.getByText(/version 1 of Microsoft's report, which has no prompt counts/)).toBeVisible();
  });

  it('says why a figure is blank when the report version is not known', () => {
    renderWithProvider(
      <MicrosoftReportFigures
        summary={summaryWith({ microsoftReportVersion: null, microsoftReportAveragePromptsPerActiveUser: null })}
      />,
    );

    expect(screen.getByText('12,345')).toBeVisible();
    expect(screen.getByText('\u2014')).toBeVisible();
    expect(screen.getByText('Blank because Microsoft\'s report did not include this figure.')).toBeVisible();
  });

  it('shows a real zero Microsoft reported as zero', () => {
    renderWithProvider(
      <MicrosoftReportFigures
        summary={summaryWith({ microsoftReportPromptsSubmitted: 0, microsoftReportAveragePromptsPerActiveUser: 0 })}
      />,
    );

    expect(screen.getAllByText('0')).toHaveLength(2);
    expect(screen.queryByText('\u2014')).not.toBeInTheDocument();
  });

  it('renders nothing when no summary has been imported, or when the server predates these figures', () => {
    const { container, unmount } = renderWithProvider(
      <MicrosoftReportFigures summary={summaryWith({ microsoftReportDate: null, microsoftReportPeriodDays: null })} />,
    );
    expect(container.textContent).toBe('');
    unmount();

    const older = summaryWith();
    delete (older as Partial<CopilotAdoptionSummary>).microsoftReportDate;
    delete (older as Partial<CopilotAdoptionSummary>).microsoftReportPeriodDays;
    const rendered = renderWithProvider(<MicrosoftReportFigures summary={older} />);
    expect(rendered.container.textContent).toBe('');
  });

  it('says the figures are tenant-wide in a view narrowed to part of the tenant', () => {
    renderWithProvider(<MicrosoftReportFigures summary={summaryWith({ unscopedSections: ['usageByApp', 'microsoftReport'] })} />);

    expect(screen.getByText('Tenant-wide')).toBeVisible();
  });

  it('offers the SQL behind the figures only when it is given', () => {
    const { unmount } = renderWithProvider(<MicrosoftReportFigures summary={summaryWith()} />);
    expect(screen.queryByRole('button', { name: 'SQL' })).not.toBeInTheDocument();
    unmount();

    renderWithProvider(<MicrosoftReportFigures summary={summaryWith()} sql="SELECT 1" />);
    expect(screen.getByRole('button', { name: 'SQL' })).toBeVisible();
  });

  it('formats the figures, the period and the date in Spanish', async () => {
    await loadCatalog('es');
    renderWithProvider(<MicrosoftReportFigures summary={summaryWith()} />, { language: 'es' });

    expect(screen.getByText('Informe de uso de Copilot de Microsoft')).toBeVisible();
    expect(screen.getByText('periodo de 28 días hasta el 5 oct 2026')).toBeVisible();
    expect(screen.getByText('12.345')).toBeVisible();
    expect(screen.getByText('18,2')).toBeVisible();
    expect(screen.getByText('indicaciones enviadas')).toBeVisible();
    expect(screen.getByText('indicaciones por usuario activo')).toBeVisible();
  });
});

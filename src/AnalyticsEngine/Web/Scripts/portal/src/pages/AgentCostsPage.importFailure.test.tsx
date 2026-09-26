import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';

import { loadCatalog } from '../i18n';
import { renderWithProvider } from '../test/renderWithProvider';
import type { CopilotCapacitySnapshot } from '../types/agentCosts';
import { CapacityUsedHint, ImportFailureBar } from './AgentCostsPage';

describe('ImportFailureBar', () => {
  it('labels a failing import in the reader language and still says why it is failing', async () => {
    await loadCatalog('es');
    renderWithProvider(
      <ImportFailureBar kind="copilotStudio" error="403 Forbidden: the runtime account lacks the Power Platform reader role." />,
      { language: 'es' },
    );

    expect(await screen.findByText('La importación de créditos de Copilot Studio está fallando.')).toBeInTheDocument();
    // Once some figures exist this bar is the page's only diagnosis of the failure, so the importer's error stays.
    expect(screen.getByText(/403 Forbidden: the runtime account lacks the Power Platform reader role\./)).toBeInTheDocument();
  });

  it('does the same for the Azure cost import', async () => {
    await loadCatalog('es');
    renderWithProvider(<ImportFailureBar kind="azure" error="Cost Management returned 429 Too Many Requests." />, { language: 'es' });

    expect(await screen.findByText(/Cost Management returned 429 Too Many Requests\./)).toBeInTheDocument();
    expect(screen.queryByText(/import is failing/)).not.toBeInTheDocument();
  });
});

describe('CapacityUsedHint', () => {
  const capacity = (consumptionType: string | null): CopilotCapacitySnapshot => ({
    snapshotUtc: '2026-09-08T00:00:00Z',
    consumptionAsOf: '2026-09-08T00:00:00Z',
    entitled: 25000,
    consumed: 4321.5,
    consumptionType,
    allocated: 1000,
    available: 20678.5,
    payAsYouGoConsumed: null,
    status: 'WithinCapacity',
  });

  it('labels the Power Platform consumption type in the reader language instead of showing its code', async () => {
    await loadCatalog('es');
    const { container } = renderWithProvider(<CapacityUsedHint capacity={capacity('MonthToDate')} />, { language: 'es' });

    expect(container.textContent).toContain('(Mes hasta la fecha)');
    expect(container.textContent).not.toContain('MonthToDate');
  });

  it('reads as words in English too, and shows an unrecognised type as sent', () => {
    const { container, rerender } = renderWithProvider(<CapacityUsedHint capacity={capacity('MonthToDate')} />);
    expect(container.textContent).toContain('(Month to date)');

    rerender(<CapacityUsedHint capacity={capacity('BillingPeriodToDate')} />);
    expect(container.textContent).toContain('(BillingPeriodToDate)');

    rerender(<CapacityUsedHint capacity={capacity(null)} />);
    expect(container.textContent).not.toContain('(');
  });
});

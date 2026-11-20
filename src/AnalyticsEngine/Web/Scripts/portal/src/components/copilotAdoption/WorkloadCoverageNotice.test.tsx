import { beforeAll, describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import { loadCatalog } from '../../i18n';
import { formatCount } from '../shared/KpiGrid';
import type { WorkloadCoverage } from '../../types/copilotAdoption';
import WorkloadCoverageNotice from './WorkloadCoverageNotice';

const coverage: WorkloadCoverage[] = [
  { source: 'teams', status: 'complete', reason: null, fromUtc: '2026-05-10', throughUtc: '2026-05-10',
    expectedDays: 1, verifiedDays: 1, emptyDays: 0, partialDays: 0, scopeUnverifiedDays: 0 },
  { source: 'outlook', status: 'empty', reason: null, fromUtc: '2026-05-10', throughUtc: '2026-05-10',
    expectedDays: 1, verifiedDays: 1, emptyDays: 1, partialDays: 0, scopeUnverifiedDays: 0 },
  { source: 'sharepoint', status: 'partial', reason: 'incomplete', fromUtc: '2026-05-10', throughUtc: '2026-05-10',
    expectedDays: 1, verifiedDays: 0, emptyDays: 0, partialDays: 0, incompleteDays: 1, scopeUnverifiedDays: 0 },
  { source: 'onedrive', status: 'unverified', reason: 'scopeUnverified', fromUtc: '2026-05-10', throughUtc: '2026-05-10',
    expectedDays: 1, verifiedDays: 0, emptyDays: 0, partialDays: 0, scopeUnverifiedDays: 1 },
];

beforeAll(async () => { await loadCatalog('es'); });

describe('persistent workload provenance', () => {
  it('distinguishes successful empty, partial and unverified sources without a dismiss action', () => {
    renderWithProvider(<WorkloadCoverageNotice coverage={coverage} />);
    expect(screen.getByText('Teams: Complete — 1 of 1 days verified')).toBeTruthy();
    expect(screen.getByText('Outlook: Successfully empty — 1 of 1 days verified')).toBeTruthy();
    expect(screen.getByText('SharePoint: Partial — 0 of 1 days verified')).toBeTruthy();
    expect(screen.getByText('OneDrive: Unverified — 0 of 1 days verified')).toBeTruthy();
    expect(screen.getByText(/Missing values are unknown, not zero/)).toBeTruthy();
    expect(screen.getByText(/up to about 62 or 152 days after the upgrade/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /dismiss|close/i })).toBeNull();
    expect(formatCount(null)).toBe('—');
    expect(formatCount(0)).toBe('0');
  });

  it('carries the same distinctions and recovery guidance in Spanish', () => {
    renderWithProvider(<WorkloadCoverageNotice coverage={coverage} />, { language: 'es' });
    expect(screen.getByText('Outlook: Vacía con éxito — 1 de 1 días verificados')).toBeTruthy();
    expect(screen.getByText('OneDrive: Sin verificar — 0 de 1 días verificados')).toBeTruthy();
    expect(screen.getByText(/Los valores ausentes son desconocidos, no cero/)).toBeTruthy();
    expect(screen.getByText(/sin estado duradero no hay cobertura verificada/)).toBeTruthy();
    expect(screen.getByText(/hasta unos 62 o 152 días después de la actualización/)).toBeTruthy();
  });
});

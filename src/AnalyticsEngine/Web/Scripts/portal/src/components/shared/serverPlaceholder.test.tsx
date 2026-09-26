import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';

import { loadCatalog } from '../../i18n';
import { renderWithProvider } from '../../test/renderWithProvider';
import CategoryBarChart from '../charts/CategoryBarChart';
import MatrixChart from '../charts/MatrixChart';
import StackedAreaChart from '../charts/StackedAreaChart';
import { SegmentTable } from '../copilotAdoption/adoptionShared';
import type { AdoptionSegmentRow } from '../../types/copilotAdoption';

/**
 * The placeholders the server writes into data are shown translated wherever the data is shown -
 * and a tenant's own names beside them are shown exactly as they arrived. These render the shared
 * components most of the portal's placeholders pass through.
 */
const segment = (name: string): AdoptionSegmentRow => ({
  segment: name,
  licensedUsers: 12,
  activeUsers: 6,
  habitualUsers: 3,
  neverUsedUsers: 4,
  adoptionRatePct: 50,
  averageAdoptionScore: 41,
});

describe('server placeholders on screen', () => {
  it('translates a placeholder bar and leaves the tenant\'s own label alone', async () => {
    await loadCatalog('es');
    renderWithProvider(
      <CategoryBarChart
        categories={[
          { label: '(no department)', value: 3 },
          { label: 'Contoso Finance', value: 5 },
        ]}
        valueLabel="Candidatos"
      />,
      { language: 'es' },
    );

    expect(await screen.findByText('(sin departamento)')).toBeInTheDocument();
    expect(screen.getByText('Contoso Finance')).toBeInTheDocument();
    expect(screen.queryByText('(no department)')).not.toBeInTheDocument();
  });

  it('keeps the server\'s own wording in English', () => {
    renderWithProvider(<CategoryBarChart categories={[{ label: '(no department)', value: 3 }]} valueLabel="Candidates" />);

    expect(screen.getByText('(no department)')).toBeInTheDocument();
  });

  it('translates a placeholder row in a matrix', async () => {
    await loadCatalog('es');
    renderWithProvider(
      <MatrixChart
        matrix={{
          rowLabel: 'Departamento',
          columnLabel: 'Aplicación',
          rows: ['(No department)', 'Contoso Finance'],
          columns: ['Outlook'],
          cells: [
            { row: '(No department)', column: 'Outlook', value: 4 },
            { row: 'Contoso Finance', column: 'Outlook', value: 9 },
          ],
          shadeByRow: true,
        }}
        valueLabel="Personas"
      />,
      { language: 'es' },
    );

    expect(await screen.findByText('(Sin departamento)')).toBeInTheDocument();
    expect(screen.getByText('Contoso Finance')).toBeInTheDocument();
    expect(screen.queryByText('(No department)')).not.toBeInTheDocument();
  });

  it('translates a placeholder series in a stacked chart legend', async () => {
    await loadCatalog('es');
    renderWithProvider(
      <StackedAreaChart
        series={[
          { name: 'Contoso Intranet', points: [{ weekStart: '2026-09-07', value: 30 }] },
          { name: '(other sites)', points: [{ weekStart: '2026-09-07', value: 12 }] },
        ]}
        valueLabel="Vistas de página"
      />,
      { language: 'es' },
    );

    // Named in the band's own tooltip and in the legend.
    expect((await screen.findAllByText('(otros sitios)')).length).toBeGreaterThan(0);
    expect(screen.getAllByText('Contoso Intranet').length).toBeGreaterThan(0);
    expect(screen.queryByText('(other sites)')).not.toBeInTheDocument();
  });

  it('translates the empty department bucket in an adoption table', async () => {
    await loadCatalog('es');
    renderWithProvider(
      <SegmentTable rows={[segment('(no department)'), segment('Contoso Finance')]} segmentLabel="Departamento" />,
      { language: 'es' },
    );

    expect(await screen.findByText('(sin departamento)')).toBeInTheDocument();
    expect(screen.getByText('Contoso Finance')).toBeInTheDocument();
    expect(screen.queryByText('(no department)')).not.toBeInTheDocument();
  });
});

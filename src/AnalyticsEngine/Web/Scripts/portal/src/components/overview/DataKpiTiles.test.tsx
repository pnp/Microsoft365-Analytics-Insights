import { expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import type { NamedCount } from '../../types/systemStatus';
import DataKpiTiles from './DataKpiTiles';

it('shows a figure the server could not count as a dash, not as zero, and keeps its tile', () => {
  const counts: NamedCount[] = [
    { key: 'auditEvents', name: 'Audit events', count: null, hint: null },
    { key: 'users', name: 'Users', count: 1234, hint: null },
  ];

  renderWithProvider(<DataKpiTiles counts={counts} />);

  expect(screen.getByText('-')).toBeInTheDocument();
  expect(screen.queryByText('0')).not.toBeInTheDocument();
  expect(screen.getByText('1,234')).toBeInTheDocument();
});

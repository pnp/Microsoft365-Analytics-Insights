import { expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import { loadCatalog } from '../../i18n';
import TreemapChart from './TreemapChart';

it('preserves the existing count presentation and ignores zero-sized categories by default', () => {
  renderWithProvider(<TreemapChart categories={[{ label: 'Contoso', value: 80 }, { label: 'Fabrikam', value: 20 }, { label: 'Empty', value: 0 }]} valueLabel="Prompts" />);
  expect(screen.getByText('80')).toBeInTheDocument();
  expect(screen.getByText('20')).toBeInTheDocument();
  expect(screen.queryByText('Empty')).not.toBeInTheDocument();
});

it('uses proportional areas and optional percentages with caller-supplied mode colours', () => {
  renderWithProvider(<TreemapChart literalLabels showShare height={340} valueLabel="Prompts" categories={[
    { label: 'Contoso', value: 75, color: '#0f6cbd' },
    { label: 'Fabrikam', value: 25, color: '#5c2e91' },
  ]} />);
  expect(screen.getByText('75%')).toBeInTheDocument();
  expect(screen.getByText('25%')).toBeInTheDocument();
  const large = screen.getByText('Contoso').parentElement!;
  const small = screen.getByText('Fabrikam').parentElement!;
  const area = (tile: HTMLElement) => parseFloat(tile.style.width.replace('calc(', '')) * parseFloat(tile.style.height.replace('calc(', ''));
  expect(area(large) / area(small)).toBeCloseTo(3, 5);
  expect(large).toHaveStyle({ backgroundColor: '#0f6cbd' });
  expect(small).toHaveStyle({ backgroundColor: '#5c2e91' });
  expect(large.title).toContain('75');
});

it('preserves literal custom labels even when they match server placeholders', async () => {
  await loadCatalog('es');
  renderWithProvider(<TreemapChart literalLabels showShare categories={[{ label: '(unknown)', value: 1 }]} valueLabel="Indicaciones" />, { language: 'es' });
  expect(screen.getByText('(unknown)')).toBeInTheDocument();
  expect(screen.queryByText('(desconocido)')).not.toBeInTheDocument();
  expect(screen.getByText('(unknown)').parentElement!.title).toContain('(unknown)');
});

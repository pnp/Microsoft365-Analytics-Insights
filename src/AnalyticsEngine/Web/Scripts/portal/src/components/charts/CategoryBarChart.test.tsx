import { expect, it } from 'vitest';
import { loadCatalog } from '../../i18n';
import { renderWithProvider } from '../../test/renderWithProvider';
import CategoryBarChart from './CategoryBarChart';

it.each([false, true])('uses literal labels only when requested: %s', async (literalLabels) => {
  await loadCatalog('es');
  const { getByText } = renderWithProvider(
    <CategoryBarChart literalLabels={literalLabels} valueLabel="Synthetic unit"
      categories={[{ label: '(unknown)', value: 12 }]} />,
    { language: 'es' },
  );
  expect(getByText(literalLabels ? '(unknown)' : '(desconocido)')).toBeInTheDocument();
}, 30000);

it('prints each bar\u2019s share in the reader\u2019s number format', async () => {
  await loadCatalog('es');
  const { getByText, queryByText } = renderWithProvider(
    <CategoryBarChart showShare valueLabel="Synthetic unit"
      categories={[{ label: 'Contoso A', value: 1 }, { label: 'Contoso B', value: 2 }]} />,
    { language: 'es' },
  );
  expect(getByText('33,3%')).toBeInTheDocument();
  expect(getByText('66,7%')).toBeInTheDocument();
  expect(queryByText('33.3%')).not.toBeInTheDocument();
}, 30000);

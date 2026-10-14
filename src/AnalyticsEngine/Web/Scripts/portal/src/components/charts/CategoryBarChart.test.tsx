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

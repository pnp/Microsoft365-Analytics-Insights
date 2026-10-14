import { expect, it } from 'vitest';
import { screen, within } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import { loadCatalog } from '../i18n';
import type { PromptCategoryReport } from '../api/promptCategoriesApi';
import PromptCategoryBreakdown from './PromptCategoryBreakdown';

function report(directing = 60, supervising = 30, unassigned = 10, notClassified = 1000): PromptCategoryReport {
  return {
    version: 'synthetic-version', versions: ['synthetic-version'], trend: [],
    categories: [
      { id: 'analysis', name: 'Analysis', description: 'Synthetic analysis', humanMode: 'directing' },
      { id: 'drafting', name: 'Drafting', description: 'Synthetic drafting', humanMode: 'directing' },
      { id: 'research', name: 'Research', description: 'Synthetic research', humanMode: 'supervising' },
      { id: 'other', name: 'Other', description: 'Synthetic other', humanMode: null },
    ],
    mix: [
      { categoryId: 'analysis', prompts: directing * 0.75 },
      { categoryId: 'drafting', prompts: directing * 0.25 },
      { categoryId: 'research', prompts: supervising },
      { categoryId: 'other', prompts: unassigned },
      { categoryId: 'not-classified', prompts: notClassified },
    ],
  };
}
const label = (id: string) => id;

it('sums all categories in each mode, excludes failures from shares, and identifies the assigned leader', () => {
  renderWithProvider(<PromptCategoryBreakdown report={report()} label={label} />);
  expect(screen.getByText('Most popular assigned human mode: Directing (60%)')).toBeInTheDocument();
  expect(screen.getByText('60 prompts')).toBeInTheDocument();
  expect(screen.getByText('30 prompts')).toBeInTheDocument();
  expect(screen.getByText('10 prompts')).toBeInTheDocument();
  expect(screen.getByText('Not classified: 1,000 Prompts')).toBeInTheDocument();
  const list = screen.getByRole('list', { name: 'Category counts, shares and human modes' });
  expect(within(list).getByText('45% · Directing')).toBeInTheDocument();
  expect(within(list).getByText('15% · Directing')).toBeInTheDocument();
  expect(within(list).getByText('30% · Supervising')).toBeInTheDocument();
  expect(within(list).getByText('10% · Unassigned')).toBeInTheDocument();
  expect(within(list).getAllByRole('listitem')).toHaveLength(4);
  expect(screen.getByText(/Shares of 100 displayed classified prompts/)).toBeInTheDocument();
});

it('does not claim a sole winner for a tie', () => {
  renderWithProvider(<PromptCategoryBreakdown report={report(40, 40, 20)} label={label} />);
  expect(screen.getByText('Directing and Supervising are equally popular among displayed prompts.')).toBeInTheDocument();
  expect(screen.queryByText('Most popular assigned mode')).not.toBeInTheDocument();
});

it('does not turn unassigned into an inferred mode or inflate the assigned share', () => {
  renderWithProvider(<PromptCategoryBreakdown report={report(10, 0, 90)} label={label} />);
  expect(screen.getByText('Most popular assigned human mode: Directing (10%)')).toBeInTheDocument();
  expect(screen.getByText('90 prompts')).toBeInTheDocument();
  expect(screen.getByText(/not necessarily a majority/)).toBeInTheDocument();
});

it('handles unassigned-only and unknown-category data without inventing a winner', () => {
  const data = report(0, 0, 0, 0);
  data.mix = [{ categoryId: 'unknown', prompts: 20 }];
  renderWithProvider(<PromptCategoryBreakdown report={data} label={label} />);
  expect(screen.getByText('No human mode is assigned to the displayed categories.')).toBeInTheDocument();
  expect(screen.getByText('100% · Unassigned')).toBeInTheDocument();
});

it('shows failures separately when there are no classified prompts, without an empty treemap or percentages', () => {
  renderWithProvider(<PromptCategoryBreakdown report={report(0, 0, 0, 20)} label={label} />);
  expect(screen.getByText('No privacy-eligible classified prompts are available for a category breakdown.')).toBeInTheDocument();
  expect(screen.getByText('Not classified: 20 Prompts')).toBeInTheDocument();
  expect(screen.queryByRole('img')).not.toBeInTheDocument();
  expect(screen.queryByText(/NaN/)).not.toBeInTheDocument();
});

it('localises decimal percentages and mode labels in Spanish, preserving tenant category names', async () => {
  await loadCatalog('es');
  renderWithProvider(<PromptCategoryBreakdown report={report(4, 8, 0, 0)} label={() => 'Contoso Καλημέρα'} />, { language: 'es' });
  expect(screen.getByText(/Modo humano asignado más popular: Supervisión \(66,7\s%\)/)).toBeInTheDocument();
  expect(screen.getByText(/66,7\s% · Supervisión/)).toBeInTheDocument();
  expect(screen.getAllByText('Contoso Καλημέρα').length).toBeGreaterThan(0);
});

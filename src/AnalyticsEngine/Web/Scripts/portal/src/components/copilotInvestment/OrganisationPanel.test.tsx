import { describe, expect, it } from 'vitest';
import { screen, within } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import { loadCatalog } from '../../i18n';
import OrganisationPanel from './OrganisationPanel';
import type { CopilotInvestmentSummary, InvestmentGroupRow } from '../../types/copilotInvestment';

/** A group's row, in synthetic Contoso figures. */
function group(over: Partial<InvestmentGroupRow>): InvestmentGroupRow {
  return {
    label: 'Sales',
    emptyKey: null,
    withheld: false,
    holders: 60,
    activeHolders: 45,
    idleHolders: 15,
    activeShare: 0.75,
    activeDaysPerHolderMonthly: 11,
    cost: 1650,
    valueBase: 4100,
    roiBase: 2.48,
    credits: 7000,
    creditConsumers: 40,
    studioCredits: 5000,
    unlicensedActiveUsers: 4,
    ...over,
  };
}

/** The server's row for a combination of too few people: a flag and nothing to read. */
const WITHHELD_ROW = group({
  label: null,
  emptyKey: 'otherSmallGroups',
  withheld: true,
  holders: 0,
  activeHolders: 0,
  idleHolders: 0,
  activeShare: null,
  activeDaysPerHolderMonthly: null,
  cost: null,
  valueBase: null,
  roiBase: null,
  credits: 0,
  creditConsumers: 0,
  studioCredits: 0,
  unlicensedActiveUsers: 0,
});

/** A combination that reached the minimum, so it is described like any other group. */
const COMBINED_ROW = group({
  label: null,
  emptyKey: 'otherSmallGroups',
  holders: 12,
  activeHolders: 6,
  idleHolders: 6,
  activeShare: 0.5,
  activeDaysPerHolderMonthly: 3,
  cost: 330,
  valueBase: 700,
  roiBase: 2.12,
  credits: 800,
  creditConsumers: 6,
  studioCredits: 120,
  unlicensedActiveUsers: 2,
});

/**
 * Only what the panel reads. Cast rather than filled out in full so adding a field to the summary does not break every
 * test in this file.
 */
function summaryWith(groups: InvestmentGroupRow[], foldedGroups: number): CopilotInvestmentSummary {
  return {
    investment: { currency: 'EUR' },
    monthsInWindow: 1,
    organisation: { dimension: 'department', dimensionName: null, groups, foldedGroups, minGroupSize: 5, hidden: false },
    concentration: { activeDays: null, credits: null, quadrants: [], quadrantMedianCredits: null, quadrantDaysPerMonth: 4 },
  } as unknown as CopilotInvestmentSummary;
}

function renderPanel(groups: InvestmentGroupRow[], foldedGroups: number, language: 'en' | 'es' = 'en') {
  return renderWithProvider(
    <OrganisationPanel summary={summaryWith(groups, foldedGroups)} dimension="department" onDimensionChange={() => undefined} canSeePii={false} customDimensions={[]} />,
    { language },
  );
}

const WITHHELD = 'Fewer than 5 people contribute to at least one figure: figures not shown';

describe('OrganisationPanel: the combined row of smaller groups', () => {
  it('shows a combination of too few people as dashes with the reason, never as figures or zeros', () => {
    renderPanel([group({}), WITHHELD_ROW], 3);

    const combined = screen.getByRole('row', { name: /Smaller groups combined/ });
    // The reason sits under the label, and every figure cell says it too: a dash, not a 0 that reads as a measurement.
    expect(within(combined).getByText(WITHHELD)).toBeInTheDocument();
    expect(within(combined).getAllByRole('img', { name: WITHHELD })).toHaveLength(9);
    expect(within(combined).queryByText('0')).not.toBeInTheDocument();
    expect(within(combined).queryByText(/[%\u20ac]/)).not.toBeInTheDocument();
    // How many groups were combined is still said, once, under the table.
    expect(screen.getByText('3 smaller groups are combined.')).toBeInTheDocument();
  });

  it('shows a combination that reached the minimum like any other group, and says nothing about withholding', () => {
    renderPanel([group({}), COMBINED_ROW], 3);

    const combined = screen.getByRole('row', { name: /Smaller groups combined/ });
    expect(within(combined).getByText('12')).toBeInTheDocument();
    expect(within(combined).getByText('50%')).toBeInTheDocument();
    expect(within(combined).getByText('\u20ac330')).toBeInTheDocument();
    expect(within(combined).queryByRole('img')).not.toBeInTheDocument();
    expect(screen.queryByText(WITHHELD)).not.toBeInTheDocument();
  });

  it('leaves an ordinary group untouched beside a withheld row', () => {
    renderPanel([group({}), WITHHELD_ROW], 3);

    const sales = screen.getByRole('row', { name: /Sales/ });
    expect(within(sales).getByText('60')).toBeInTheDocument();
    expect(within(sales).getByText('75%')).toBeInTheDocument();
    expect(within(sales).getByText('\u20ac1,650')).toBeInTheDocument();
    expect(within(sales).queryByRole('img')).not.toBeInTheDocument();
  });

  it('gives the reason in Spanish', async () => {
    await loadCatalog('es');
    renderPanel([group({}), WITHHELD_ROW], 3, 'es');

    const combined = screen.getByRole('row', { name: /Grupos pequeños combinados/ });
    const withheld = 'Menos de 5 personas contribuyen a alguna cifra: no se muestran las cifras';
    expect(within(combined).getByText(withheld)).toBeInTheDocument();
    expect(within(combined).getAllByRole('img', { name: withheld })).toHaveLength(9);
  });
});

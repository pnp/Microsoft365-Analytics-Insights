import { describe, expect, it } from 'vitest';
import { screen, within } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import ActionPlan from './ActionPlan';
import type { AdoptionActionSummary } from '../../types/copilotAdoption';

describe('ActionPlan guidance links', () => {
  it('renders Microsoft guidance once on the action card', () => {
    const actions: AdoptionActionSummary[] = [
      {
        code: 'coach',
        label: 'Build a first habit',
        description: 'Occasional use only.',
        users: 12,
        sharePct: 25,
        guidanceLinks: [
          {
            actionCode: 'coach',
            title: 'Copilot Academy',
            url: 'https://aka.ms/copilot-academy',
            expectedTitle: 'Viva Learning',
            audience: 'enablementOwner',
            publisher: 'Microsoft',
            catalogueVersion: '2026.09.16',
          },
        ],
      },
    ];

    renderWithProvider(<ActionPlan actions={actions} />);

    const card = screen.getByText('Occasional use only.').closest('div');
    expect(card).not.toBeNull();
    expect(within(card as HTMLElement).getByText("Microsoft's guidance for this kind of user:")).toBeInTheDocument();
    const links = within(card as HTMLElement).getAllByRole('link', { name: 'Copilot Academy' });
    expect(links).toHaveLength(1);
    expect(links[0]).toHaveAttribute('href', 'https://aka.ms/copilot-academy');
  });
});

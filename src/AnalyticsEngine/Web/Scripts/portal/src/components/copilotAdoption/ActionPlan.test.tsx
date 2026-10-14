import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import ActionPlan from './ActionPlan';
import type { AdoptionActionSummary, AdoptionGuidanceLink } from '../../types/copilotAdoption';

describe('ActionPlan guidance links', () => {
  const coach = (link: Partial<AdoptionGuidanceLink>): AdoptionActionSummary[] => [
    {
      code: 'coach',
      label: 'Build a first habit',
      description: 'Occasional use only.',
      users: 12,
      sharePct: 25,
      guidanceLinks: [
        {
          actionCode: 'coach',
          titleKey: 'copilotAcademy',
          title: 'Copilot Academy',
          url: 'https://aka.ms/copilot-academy',
          expectedTitle: 'Viva Learning',
          audience: 'enablementOwner',
          publisher: 'Microsoft',
          catalogueVersion: '2026.10.07',
          ...link,
        },
      ],
    },
  ];

  it('renders Microsoft guidance once on the action card', () => {
    renderWithProvider(<ActionPlan actions={coach({})} />);

    const card = screen.getByText('Occasional use only.').closest('div');
    expect(card).not.toBeNull();
    expect(within(card as HTMLElement).getByText("Microsoft's guidance for this kind of user:")).toBeInTheDocument();
    const links = within(card as HTMLElement).getAllByRole('link', { name: 'Copilot Academy' });
    expect(links).toHaveLength(1);
    expect(links[0]).toHaveAttribute('href', 'https://aka.ms/copilot-academy');
  });

  it('translates a known guidance title by its stable key on a Spanish page', async () => {
    renderWithProvider(
      <ActionPlan
        actions={coach({
          titleKey: 'workTrendIndex2026',
          title: '2026 Work Trend Index',
          url: 'https://www.microsoft.com/en-us/worklab/work-trend-index/agents-human-agency-and-the-opportunity-for-every-organization',
        })}
      />,
      { language: 'es' },
    );

    // The Spanish catalog arrives after the first render.
    const link = await screen.findByRole('link', { name: 'Work Trend Index 2026' });
    expect(link).toHaveAttribute(
      'href',
      'https://www.microsoft.com/en-us/worklab/work-trend-index/agents-human-agency-and-the-opportunity-for-every-organization',
    );
    expect(screen.queryByRole('link', { name: '2026 Work Trend Index' })).toBeNull();
  });

  it("falls back to the server's title for a key this build does not know", async () => {
    renderWithProvider(
      <ActionPlan actions={coach({ titleKey: 'aResourceFromANewerServer', title: 'Contoso adoption handbook' })} />,
      { language: 'es' },
    );

    // Wait for the Spanish catalog, so the fallback is proven on a Spanish page rather than an English one.
    await waitFor(() => expect(document.documentElement.lang).toBe('es-ES'));
    expect(await screen.findByText('Guía de Microsoft para este tipo de usuario:', { exact: false })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Contoso adoption handbook' })).toBeInTheDocument();
  });
});

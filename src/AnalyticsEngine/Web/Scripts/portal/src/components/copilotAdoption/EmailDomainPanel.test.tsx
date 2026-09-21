import { describe, it, expect, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import EmailDomainPanel from './EmailDomainPanel';
import type {
  AdoptionDomainRow,
  CopilotAdoptionOptions,
  CopilotAdoptionSummary,
} from '../../types/copilotAdoption';

/**
 * Only the fields the panel reads. Cast rather than filled out in full so adding an option to the
 * interface does not break every test in this file.
 */
const OPTIONS = {
  minSeatsPerSegment: 5,
  championScore: 75,
  establishedScore: 50,
  developingScore: 25,
} as CopilotAdoptionOptions;

function domain(over: Partial<AdoptionDomainRow>): AdoptionDomainRow {
  return {
    segment: 'contoso.com',
    licensedUsers: 100,
    activeUsers: 70,
    habitualUsers: 40,
    neverUsedUsers: 20,
    adoptionRatePct: 70,
    averageAdoptionScore: 55,
    reclaimableSeats: 12,
    interactionsPerLicensedUser: 18.4,
    unlicensedActiveUsers: 5,
    recommendedForLicence: 2,
    coworkPrimeCandidates: 0,
    external: false,
    ...over,
  };
}

function summaryWith(
  domains: AdoptionDomainRow[],
  over: Partial<CopilotAdoptionSummary> = {},
): CopilotAdoptionSummary {
  return {
    emailDomains: domains,
    options: OPTIONS,
    coworkReadinessAvailable: false,
    ...over,
  } as CopilotAdoptionSummary;
}

/** The organisation that ran the rollout, and the one it acquired and never enabled. */
const DOMAINS: AdoptionDomainRow[] = [
  domain({
    segment: 'fabrikam.com',
    licensedUsers: 80,
    activeUsers: 8,
    habitualUsers: 2,
    neverUsedUsers: 60,
    adoptionRatePct: 10,
    averageAdoptionScore: 9,
    reclaimableSeats: 55,
    interactionsPerLicensedUser: 1.2,
    unlicensedActiveUsers: 30,
    recommendedForLicence: 11,
  }),
  domain({ segment: 'contoso.com' }),
];

function renderPanel(
  domains: AdoptionDomainRow[] = DOMAINS,
  props: Partial<Parameters<typeof EmailDomainPanel>[0]> = {},
) {
  return renderWithProvider(<EmailDomainPanel summary={summaryWith(domains)} {...props} />);
}

function rowFor(segment: string): HTMLElement {
  const cell = screen.getByText(segment);
  return cell.closest('tr') as HTMLElement;
}

describe('EmailDomainPanel', () => {
  it('compares the organisations sharing the tenant, one row each', () => {
    renderPanel();

    expect(within(rowFor('fabrikam.com')).getByText('10%')).toBeTruthy();
    expect(within(rowFor('contoso.com')).getByText('70%')).toBeTruthy();
  });

  it('puts the unlicensed and candidate counts on the same row as the idle seats', () => {
    // The point of the view: idle seats next to heavy unlicensed Chat use is a seat-allocation
    // problem that can be fixed at no cost, and it is only visible when both are on one row.
    renderPanel();

    const row = within(rowFor('fabrikam.com'));

    expect(row.getByText('55')).toBeTruthy();
    expect(row.getByText('30')).toBeTruthy();
    expect(row.getByText('11')).toBeTruthy();
  });

  it('shows a dash for a domain with no seats, never 0%', () => {
    // A domain with no licences was never offered Copilot. Rendering 0% would read as "this
    // organisation ignores it", which is the opposite of the truth and the opposite decision.
    renderPanel([
      ...DOMAINS,
      domain({
        segment: 'northwind.example',
        licensedUsers: 0,
        activeUsers: 0,
        habitualUsers: 0,
        neverUsedUsers: 0,
        adoptionRatePct: 0,
        averageAdoptionScore: 0,
        reclaimableSeats: 0,
        interactionsPerLicensedUser: 0,
        unlicensedActiveUsers: 14,
        recommendedForLicence: 9,
      }),
    ]);

    const row = within(rowFor('northwind.example'));

    expect(row.getByText(/no seats/)).toBeTruthy();
    expect(row.queryByText('0%')).toBeNull();

    // Its unlicensed demand still shows - that is the actionable part of the row.
    expect(row.getByText('14')).toBeTruthy();
    expect(row.getByText('9')).toBeTruthy();
  });

  it('flags a guest domain as external', () => {
    renderPanel([...DOMAINS, domain({ segment: 'partner.example', external: true })]);

    expect(within(rowFor('partner.example')).getByText('External')).toBeTruthy();
    expect(within(rowFor('contoso.com')).queryByText('External')).toBeNull();
  });

  it('renders a non-ASCII domain intact', () => {
    // Internationalised domain names are real, and a report that mangles one names the wrong company.
    renderPanel([...DOMAINS, domain({ segment: 'παράδειγμα.gr' })]);

    expect(screen.getByText('παράδειγμα.gr')).toBeTruthy();
  });

  it('narrows the whole report when a row is filtered, and clears when it is filtered again', async () => {
    const onSelectDomain = vi.fn();
    const user = userEvent.setup();

    const { rerender } = renderWithProvider(
      <EmailDomainPanel summary={summaryWith(DOMAINS)} onSelectDomain={onSelectDomain} />,
    );

    await user.click(within(rowFor('fabrikam.com')).getByRole('button', { name: 'Filter' }));
    expect(onSelectDomain).toHaveBeenCalledWith('fabrikam.com');

    rerender(
      <EmailDomainPanel
        summary={summaryWith(DOMAINS)}
        selectedDomain="fabrikam.com"
        onSelectDomain={onSelectDomain}
      />,
    );

    await user.click(within(rowFor('fabrikam.com')).getByRole('button', { name: 'Clear' }));
    expect(onSelectDomain).toHaveBeenLastCalledWith(null);
  });

  it('offers no filter buttons where the page-wide filter does not apply', () => {
    renderPanel();

    expect(screen.queryByRole('button', { name: 'Filter' })).toBeNull();
  });

  it('hides the Cowork column until the readiness analysis has actually run', () => {
    const domains = [domain({ segment: 'contoso.com', coworkPrimeCandidates: 7 }), domain({ segment: 'fabrikam.com' })];

    const { rerender } = renderWithProvider(
      <EmailDomainPanel summary={summaryWith(domains, { coworkReadinessAvailable: false })} />,
    );
    expect(screen.queryByText('Cowork candidates')).toBeNull();

    rerender(<EmailDomainPanel summary={summaryWith(domains, { coworkReadinessAvailable: true })} />);
    expect(screen.getByText('Cowork candidates')).toBeTruthy();
  });

  it('explains itself rather than showing an empty table when there is nothing to compare', () => {
    renderPanel([]);
    expect(screen.getByText(/No email domain has enough people/)).toBeTruthy();
  });

  it('says so plainly when the tenant only has one domain', () => {
    // A table comparing an organisation with itself reads as a broken chart.
    renderPanel([domain({ segment: 'contoso.com' })]);

    expect(screen.getByText(/Everybody in this analysis is on one email domain/)).toBeTruthy();
  });
});

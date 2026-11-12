import { describe, it, expect, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import EmailDomainPanel from './EmailDomainPanel';
import { loadCatalog } from '../../i18n';
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

  it('writes interactions per licence in the reader\u2019s number format', async () => {
    await loadCatalog('es');
    renderWithProvider(<EmailDomainPanel summary={summaryWith(DOMAINS)} />, { language: 'es' });

    expect(within(rowFor('fabrikam.com')).getByText('1,2')).toBeTruthy();
    expect(within(rowFor('contoso.com')).getByText('18,4')).toBeTruthy();
    expect(within(rowFor('contoso.com')).queryByText('18.4')).toBeNull();
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

describe('EmailDomainPanel for a reader without See PII (#844)', () => {
  /**
   * Each population on a row floored on its own: contoso's 3 unlicensed Chat users and fabrikam's 3 seat holders are
   * hidden, and northwind's Chat count with them, because the tenant's total less the counts shown would give a
   * hidden one back. What the server sends: null figures, each with its reason.
   */
  const WITHHELD: AdoptionDomainRow[] = [
    domain({
      segment: 'contoso.com',
      licensedUsers: 20,
      activeUsers: 12,
      habitualUsers: 6,
      neverUsedUsers: 8,
      adoptionRatePct: 60,
      unlicensedActiveUsers: null,
      unlicensedUsersWithheld: 'fewPeople',
      recommendedForLicence: 7,
      minimumPeople: 5,
    }),
    domain({
      segment: 'fabrikam.com',
      licensedUsers: null,
      activeUsers: null,
      habitualUsers: null,
      neverUsedUsers: null,
      adoptionRatePct: null,
      averageAdoptionScore: null,
      reclaimableSeats: null,
      interactionsPerLicensedUser: null,
      coworkPrimeCandidates: null,
      seatHoldersWithheld: 'fewPeople',
      unlicensedActiveUsers: 20,
      recommendedForLicence: 0,
      minimumPeople: 5,
    }),
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
      unlicensedActiveUsers: null,
      unlicensedUsersWithheld: 'revealsSmallerGroup',
      recommendedForLicence: 0,
      minimumPeople: 5,
    }),
  ];

  it('shows a hidden population as hidden and says why - never 0, and hidden seat holders are not "no seats"', () => {
    renderPanel(WITHHELD);

    const contoso = within(rowFor('contoso.com'));
    expect(contoso.getByLabelText('Hidden: fewer than 5 people on this domain used Copilot Chat without a licence')).toHaveTextContent('\u2014');
    expect(contoso.getByText('20')).toBeTruthy();
    expect(contoso.getByText('60%')).toBeTruthy();

    const fabrikam = within(rowFor('fabrikam.com'));
    expect(fabrikam.getByText('Hidden: fewer than 5 people on this domain hold a Copilot seat')).toBeTruthy();
    expect(fabrikam.queryByText(/no seats/)).toBeNull();
    expect(fabrikam.queryByText('0%')).toBeNull();
    expect(fabrikam.getByText('20')).toBeTruthy();

    const northwind = within(rowFor('northwind.example'));
    expect(northwind.getByLabelText(
      'Hidden: with the tenant total, this count would reveal a domain where fewer than 5 people used Copilot Chat without a licence',
    )).toHaveTextContent('\u2014');
    expect(northwind.getByText(/no seats/)).toBeTruthy();

    expect(screen.getByText(/a group of fewer than 5 people is hidden, so that nobody can be singled out/)).toBeTruthy();
    expect(screen.getByText(/One more domain is hidden in a column/)).toBeTruthy();
  });

  it('says the same in Spanish', async () => {
    await loadCatalog('es');
    renderWithProvider(<EmailDomainPanel summary={summaryWith(WITHHELD)} />, { language: 'es' });

    expect(await screen.findByText('Oculto: menos de 5 personas de este dominio tienen un puesto de Copilot', {}, { timeout: 5000 })).toBeTruthy();
    expect(within(rowFor('contoso.com')).getByLabelText('Oculto: menos de 5 personas de este dominio usaron Copilot Chat sin licencia')).toBeTruthy();
    expect(within(rowFor('northwind.example')).getByLabelText(
      'Oculto: con el total del inquilino, este recuento revelaría un dominio en el que menos de 5 personas usaron Copilot Chat sin licencia',
    )).toBeTruthy();
    expect(screen.getByText(/un grupo de menos de 5 personas se oculta/)).toBeTruthy();
    expect(screen.queryByText(/Hidden/)).toBeNull();
  });

  it('adds nothing when no figure is hidden - the table a reader with See PII is given', () => {
    renderPanel();

    expect(screen.queryByLabelText(/Hidden/)).toBeNull();
    expect(screen.queryByText(/is hidden/)).toBeNull();
  });
});

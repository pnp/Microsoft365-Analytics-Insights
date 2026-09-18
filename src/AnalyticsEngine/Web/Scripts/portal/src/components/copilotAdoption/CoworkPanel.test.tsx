import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import type {
  CopilotAdoptionOptions,
  CopilotAdoptionSummary,
  CoworkReadinessPage,
  CoworkReadinessRow,
} from '../../types/copilotAdoption';

const fetchCowork = vi.fn();

vi.mock('../../api/copilotAdoptionApi', () => ({
  fetchCowork: (...args: unknown[]) => fetchCowork(...args),
  coworkExportUrl: () => '/api/CopilotAdoption/cowork/export?windowDays=28',
}));

// Imported after the mock so the panel picks up the stubbed module.
const { default: CoworkPanel } = await import('./CoworkPanel');

const OPTIONS = {
  coworkLoadMinScore: 50,
  coworkFluencyMinScore: 50,
  coworkRegularMinActiveDays: 3,
  coworkAgentFamiliarityUplift: 10,
  coworkMeetingWeight: 35,
  coworkEmailWeight: 25,
  coworkCollaborationWeight: 20,
  coworkDocumentWeight: 20,
  coworkMeetingTarget: 5,
  coworkEmailTarget: 80,
  coworkCollaborationTarget: 50,
  coworkDocumentTarget: 30,
} as CopilotAdoptionOptions;

function row(overrides: Partial<CoworkReadinessRow>): CoworkReadinessRow {
  return {
    userId: 1,
    userPrincipalName: 'aisha.rahman@contoso.com',
    mail: null,
    department: 'Operations',
    jobTitle: null,
    country: null,
    officeLocation: null,
    companyName: null,
    manager: null,
    accountEnabled: true,
    coworkInteractions: 0,
    coworkActiveDays: 0,
    lastCoworkInteractionUtc: null,
    usedCowork: false,
    regularCoworkUser: false,
    coordinationLoadScore: 80,
    fluencyScore: 70,
    adoptionScore: 60,
    agentsUsed: 1,
    collaborationScore: 50,
    meetingScore: 90,
    emailScore: 70,
    documentScore: 40,
    teamsMessages: 40,
    teamsMeetings: 6,
    emailsSent: 30,
    emailsRead: 50,
    filesViewedOrEdited: 12,
    lastM365ActivityUtc: null,
    tier: 'primeCandidate',
    tierLabel: 'Prime candidate',
    basis: 'inference',
    recommendForPolicy: true,
    rationale: 'Predicted fit - not yet measured.',
    totalCopilotCredits: null,
    ...overrides,
  };
}

function summary(overrides: Partial<CopilotAdoptionSummary> = {}): CopilotAdoptionSummary {
  return {
    options: OPTIONS,
    coworkReadinessAvailable: true,
    coworkScoredUsers: 2,
    coworkEstablishedUsers: 1,
    coworkTriallingUsers: 0,
    coworkPrimeCandidates: 1,
    coworkBuildFluencyFirst: 0,
    coworkRecommendedForPolicy: 2,
    coworkAverageCoordinationLoad: 70,
    coworkAverageFluency: 60,
    coworkTiers: [
      {
        code: 'established',
        label: 'Established',
        basis: 'evidence',
        description: 'Already part of how they work.',
        users: 1,
        sharePct: 50,
      },
      {
        code: 'primeCandidate',
        label: 'Prime candidate',
        basis: 'inference',
        description: 'Enable these people first.',
        users: 1,
        sharePct: 50,
      },
    ],
    coworkQuadrant: [],
    coworkByDepartment: [],
    coworkCreditPosition: {
      available: false,
      snapshotUtc: null,
      entitled: null,
      consumed: null,
      available_credits: null,
      payAsYouGoConsumed: null,
      status: null,
      perUserCreditsAvailable: false,
    },
    coworkValueEstimate: {
      isModelled: true,
      cohortUsers: 0,
      addressableMeetings: 0,
      addressableMailThreads: 0,
      addressableDocuments: 0,
      hoursPerMonthLow: 0,
      hoursPerMonthHigh: 0,
      assumptions: [],
    },
    ...overrides,
  } as CopilotAdoptionSummary;
}

function page(rows: CoworkReadinessRow[]): CoworkReadinessPage {
  return { total: rows.length, skip: 0, take: 50, rows, warnings: [] };
}

function render(s: CopilotAdoptionSummary) {
  return renderWithProvider(
    <CoworkPanel windowDays={28} summary={s} filterOptions={null} options={s.options} />,
  );
}

describe('CoworkPanel', () => {
  beforeEach(() => {
    fetchCowork.mockReset();
    fetchCowork.mockResolvedValue(page([row({})]));
  });

  it('explains a missing import instead of showing an empty candidate list', async () => {
    // "No candidates" is a finding; a missing usage-report import is a fault. Rendering the first as
    // the second would tell an admin nobody needs Cowork when in fact nothing was measured.
    render(summary({ coworkReadinessAvailable: false }));

    expect(screen.getByText(/could not be assessed/)).toBeTruthy();
    expect(screen.getAllByText(/usage report/i).length).toBeGreaterThan(0);
    expect(fetchCowork).not.toHaveBeenCalled();
  });

  it('says Cowork has no licence of its own', async () => {
    render(summary());

    expect(screen.getByText(/no licence of its own/)).toBeTruthy();
    expect(screen.getByText(/spending policy scoped to users or groups/)).toBeTruthy();
  });

  it('badges an observed tier differently from a predicted one', async () => {
    render(summary());

    const established = screen.getByText('Established').closest('button') as HTMLElement;
    const prime = screen.getAllByText('Prime candidate')[0].closest('button') as HTMLElement;

    expect(within(established).getByText('Observed')).toBeTruthy();
    expect(within(prime).getByText('Predicted')).toBeTruthy();
  });

  it('marks a predicted row in the table as predicted', async () => {
    render(summary());

    await waitFor(() => expect(screen.getByText('aisha.rahman@contoso.com')).toBeTruthy());

    const table = screen.getByRole('table');
    expect(within(table).getByText('Predicted')).toBeTruthy();
  });

  it('hides the credit block entirely when the credit import has not run', async () => {
    render(summary());

    expect(screen.queryByText('Copilot Credit headroom')).toBeNull();
  });

  it('labels the credit pool as shared rather than as Cowork spend', async () => {
    // Microsoft meters Cowork against the shared Copilot Credits pool with no per-workload split, so
    // presenting this as a Cowork bill would be a fabrication.
    render(
      summary({
        coworkCreditPosition: {
          available: true,
          snapshotUtc: '2026-08-20T00:00:00Z',
          entitled: 100000,
          consumed: 42000,
          available_credits: 58000,
          payAsYouGoConsumed: null,
          status: 'WithinCapacity',
          perUserCreditsAvailable: false,
        },
      }),
    );

    expect(screen.getByText('Copilot Credit headroom')).toBeTruthy();
    expect(screen.getByText(/shared Copilot Credits pool, not Cowork-only spend/)).toBeTruthy();
  });

  it('omits the per-user credit column when no per-user rows exist', async () => {
    render(summary());
    await waitFor(() => expect(screen.getByText('aisha.rahman@contoso.com')).toBeTruthy());

    expect(screen.queryByText('All Copilot Credits')).toBeNull();
  });

  it('shows an unattributable credit as a dash, never as zero', async () => {
    fetchCowork.mockResolvedValue(page([row({ totalCopilotCredits: null })]));

    render(
      summary({
        coworkCreditPosition: {
          available: true,
          snapshotUtc: null,
          entitled: null,
          consumed: null,
          available_credits: null,
          payAsYouGoConsumed: null,
          status: null,
          perUserCreditsAvailable: true,
        },
      }),
    );

    await waitFor(() => expect(screen.getByText('aisha.rahman@contoso.com')).toBeTruthy());

    // A zero here would read as "this person costs nothing", which is a different claim entirely.
    const table = screen.getByRole('table');
    expect(within(table).getByText('\u2014')).toBeTruthy();
  });

  it('never renders the estimate without its assumptions', async () => {
    render(
      summary({
        coworkValueEstimate: {
          isModelled: true,
          cohortUsers: 40,
          addressableMeetings: 4800,
          addressableMailThreads: 64000,
          addressableDocuments: 9600,
          hoursPerMonthLow: 300,
          hoursPerMonthHigh: 600,
          assumptions: ['Assumes 5 minutes per meeting.', 'Time saved is NOT measured by this product.'],
        },
      }),
    );

    expect(screen.getByText(/a model, not a measurement/)).toBeTruthy();
    expect(screen.getByText('Assumes 5 minutes per meeting.')).toBeTruthy();
    expect(screen.getByText(/Time saved is NOT measured/)).toBeTruthy();
  });

  it('shows the estimate as a range rather than a single number', async () => {
    render(
      summary({
        coworkValueEstimate: {
          isModelled: true,
          cohortUsers: 40,
          addressableMeetings: 4800,
          addressableMailThreads: 64000,
          addressableDocuments: 9600,
          hoursPerMonthLow: 300,
          hoursPerMonthHigh: 600,
          assumptions: ['Assumes 5 minutes per meeting.'],
        },
      }),
    );

    expect(screen.getByText(/300-600 hours/)).toBeTruthy();
  });

  it('never converts the modelled hours into money', async () => {
    render(
      summary({
        coworkValueEstimate: {
          isModelled: true,
          cohortUsers: 40,
          addressableMeetings: 4800,
          addressableMailThreads: 64000,
          addressableDocuments: 9600,
          hoursPerMonthLow: 300,
          hoursPerMonthHigh: 600,
          assumptions: ['No monetary value is shown. This product has no defensible fully-loaded hourly rate, so it does not invent one.'],
        },
      }),
    );

    // Hours are the whole output. A currency figure derived from a modelled number is how a model
    // ends up quoted as a saving, which is exactly what this estimate must not become.
    expect(screen.getByText(/300-600 hours/)).toBeTruthy();
    expect(screen.queryByText(/at the configured loaded hourly cost/)).toBeNull();
    expect(document.body.textContent).not.toMatch(/[£$€]\s?\d/);
  });

  it('hides the estimate entirely for an empty cohort', async () => {
    render(summary());

    expect(screen.queryByText(/a model, not a measurement/)).toBeNull();
  });
});

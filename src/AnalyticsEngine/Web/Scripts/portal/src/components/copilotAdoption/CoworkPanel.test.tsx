import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import { PRINT_ROW_LIMIT, requestPrint, resetPrintPreparation } from '../shared/printPreparation';
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
const { resetTimeSavedStore, TIME_SAVED_STORAGE_KEY } = await import('./coworkTimeSaved');

const OPTIONS = {
  workingDaysPerWeek: 5,
  habitBucketNormalisationDays: 28,
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
  copilotMinutesSavedPerMeeting: 5,
  copilotMinutesSavedPerMailThread: 1,
  copilotMinutesSavedPerDocument: 3,
  coworkEstimateLowerBoundRatio: 0.5,
  coworkMinutesSavedPerTask: 6,
  coworkOrganiseMeetingsShare: 0.25,
  coworkOrganiseMeetingsMinutes: 6,
  coworkPrepareMeetingsShare: 0.1,
  coworkPrepareMeetingsMinutes: 6,
  coworkSendEmailShare: 0.05,
  coworkSendEmailMinutes: 6,
  coworkPostInTeamsShare: 0.01,
  coworkPostInTeamsMinutes: 6,
  coworkCreateDocumentsShare: 0.02,
  coworkCreateDocumentsMinutes: 6,
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
      coworkTaskUsers: 0,
      observedCoworkTasks: 0,
      projectedCoworkUsers: 0,
      activities: [],
      projectedCoworkTasks: 0,
      coworkTasks: 0,
      observedTasksPerPersonPerMonth: 0,
      observedTaskRateUsers: 0,
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

/** The tab is sectioned now; most assertions start by opening the section they are about. */
async function openSection(user: ReturnType<typeof userEvent.setup>, name: RegExp) {
  await user.click(screen.getByRole('tab', { name }));
  return screen.getByRole('tabpanel', { name });
}

// The people ready for Cowork now: 40 tasks already in Microsoft's report from 4 people, and the 36
// people not yet running Cowork tasks modelled from their own work at the product defaults -
// 720 meetings organised x 25% = 180, 2,880 attended x 10% = 288, 14,400 emails x 5% = 720, 36,000
// Teams messages x 1% = 360 and 7,200 files x 2% = 144: 1,692 pieces of work. (40 + 1,692) x 6
// minutes = 10,392 minutes = 173.2 hours, 86.6 at the 50% conservative end.
const ESTIMATE = {
  isModelled: true,
  cohortUsers: 40,
  coworkTaskUsers: 4,
  observedCoworkTasks: 40,
  projectedCoworkUsers: 36,
  activities: [
    { activity: 'organiseMeetings' as const, volumePerMonth: 720 },
    { activity: 'prepareMeetings' as const, volumePerMonth: 2880 },
    { activity: 'sendEmail' as const, volumePerMonth: 14400 },
    { activity: 'postInTeams' as const, volumePerMonth: 36000 },
    { activity: 'createDocuments' as const, volumePerMonth: 7200 },
  ],
  projectedCoworkTasks: 1692,
  coworkTasks: 1732,
  observedTasksPerPersonPerMonth: 10,
  observedTaskRateUsers: 4,
  hoursPerMonthLow: 87,
  hoursPerMonthHigh: 173,
  assumptions: ['Assumes Cowork saves 6 minutes on each meeting it organises.', 'Time saved is NOT measured by this product.'],
};

// Every Copilot seat holder, the ceiling: the same 4 observed people, and 396 modelled, doing eleven
// times the work of the 36 above - 18,612 pieces of work. (40 + 18,612) x 6 = 111,912 minutes =
// 1,865.2 hours, 932.6 conservative.
const FULL_ESTIMATE = {
  ...ESTIMATE,
  cohortUsers: 400,
  projectedCoworkUsers: 396,
  activities: ESTIMATE.activities.map((a) => ({ ...a, volumePerMonth: a.volumePerMonth * 11 })),
  projectedCoworkTasks: 18612,
  coworkTasks: 18652,
  hoursPerMonthLow: 933,
  hoursPerMonthHigh: 1865,
};

/** A summary carrying both Cowork cohorts, as the server sends one for a tenant with readiness data. */
function withEstimates(overrides: Partial<CopilotAdoptionSummary> = {}) {
  return summary({
    coworkScoredUsers: 400,
    coworkRecommendedForPolicy: 40,
    coworkValueEstimate: ESTIMATE,
    coworkFullRolloutEstimate: FULL_ESTIMATE,
    ...overrides,
  });
}

/**
 * Renders the panel and lets the people list's first load land. Without the wait that load resolves
 * after the test has finished, and React reports its state update as outside act().
 */
async function renderSettled(s: CopilotAdoptionSummary) {
  const result = render(s);
  await waitFor(() => expect(fetchCowork).toHaveBeenCalled());
  return result;
}

describe('CoworkPanel', () => {
  beforeEach(() => {
    fetchCowork.mockReset();
    fetchCowork.mockResolvedValue(page([row({})]));
    resetTimeSavedStore();
  });

  it('explains a missing import instead of showing an empty candidate list', async () => {
    // "No candidates" is a finding; a missing usage-report import is a fault. Rendering the first as
    // the second would tell an admin nobody needs Cowork when in fact nothing was measured.
    render(summary({ coworkReadinessAvailable: false }));

    expect(screen.getByText(/could not be assessed/)).toBeTruthy();
    expect(screen.getAllByText(/usage report/i).length).toBeGreaterThan(0);
    expect(fetchCowork).not.toHaveBeenCalled();
  });

  it('says Cowork has no licence of its own, beside the list it produces', async () => {
    const user = userEvent.setup();
    render(summary());

    const people = await openSection(user, /People to enable/);
    expect(within(people).getByText(/no licence of its own/)).toBeTruthy();
    expect(within(people).getByText(/spending policy scoped to users or groups/)).toBeTruthy();
  });

  it('badges an observed tier differently from a predicted one', async () => {
    const user = userEvent.setup();
    render(summary());

    const readiness = await openSection(user, /Readiness/);
    const established = within(readiness).getByText('Established').closest('button') as HTMLElement;
    const prime = within(readiness).getByText('Prime candidate').closest('button') as HTMLElement;

    expect(within(established).getByText('Observed')).toBeTruthy();
    expect(within(prime).getByText('Predicted')).toBeTruthy();
  });

  it('opens the people list narrowed to a tier when its card is selected', async () => {
    // The tier cards and the list are in different sections now, so a card has to take the reader to
    // exactly the people it counted - and the list has to show that it has been narrowed.
    const user = userEvent.setup();
    render(summary());
    await waitFor(() => expect(fetchCowork).toHaveBeenCalled());

    const readiness = await openSection(user, /Readiness/);
    fetchCowork.mockClear();
    await user.click(within(readiness).getByText('Established').closest('button') as HTMLElement);

    expect(screen.getByRole('tabpanel', { name: /People to enable/ })).toBeTruthy();
    await waitFor(() => expect(fetchCowork).toHaveBeenCalled());
    expect((fetchCowork.mock.calls.at(-1)![1] as { tiers: string[] }).tiers).toEqual(['established']);
    expect(
      (screen.getByRole('combobox', { name: 'Filter Cowork candidates by verdict' }) as HTMLSelectElement).value,
    ).toBe('established');
  });

  it('marks a predicted row in the table as predicted', async () => {
    const user = userEvent.setup();
    render(summary());

    const people = await openSection(user, /People to enable/);
    await waitFor(() => expect(within(people).getByText('aisha.rahman@contoso.com')).toBeTruthy());

    const table = within(people).getByRole('table');
    expect(within(table).getByText('Predicted')).toBeTruthy();
  });

  it('hides the credit block entirely when the credit import has not run', async () => {
    render(summary());

    expect(screen.queryByText('Copilot Credit headroom')).toBeNull();
  });

  it('labels the credit pool as shared rather than as Cowork spend', async () => {
    // Microsoft meters Cowork against the shared Copilot Credits pool with no per-workload split, so
    // presenting this as a Cowork bill would be a fabrication.
    const user = userEvent.setup();
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

    const rollout = await openSection(user, /Rollout plan/);
    expect(within(rollout).getByText('Copilot Credit headroom')).toBeTruthy();
    expect(within(rollout).getByText(/shared Copilot Credits pool, not Cowork-only spend/)).toBeTruthy();
  });

  it('never gives the optional per-user credit import a column, even when it has rows', async () => {
    // The Power Platform per-user credit import is optional and, on most tenants, not configured.
    // A column would cost every tenant horizontal space for a figure most of them cannot populate,
    // in a table already wider than the screen - so it lives in the expander instead.
    const user = userEvent.setup();
    fetchCowork.mockResolvedValue(page([row({ totalCopilotCredits: 197.8 })]));

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

    const people = await openSection(user, /People to enable/);
    await waitFor(() => expect(within(people).getByText('aisha.rahman@contoso.com')).toBeTruthy());

    const header = within(within(people).getByRole('table')).getAllByRole('columnheader');
    expect(header.map((h) => h.textContent)).not.toContain('All Copilot Credits');

    await user.click(within(people).getByRole('button', { name: /Show the full assessment/ }));

    expect(await within(people).findByText('All Copilot Credits')).toBeTruthy();
  });

  it('omits the per-user credit figures entirely when no per-user rows exist', async () => {
    const user = userEvent.setup();
    render(summary());
    const people = await openSection(user, /People to enable/);
    await waitFor(() => expect(within(people).getByText('aisha.rahman@contoso.com')).toBeTruthy());

    expect(screen.queryByText('All Copilot Credits')).toBeNull();

    await user.click(within(people).getByRole('button', { name: /Show the full assessment/ }));

    // The expander opened - so the absence below is a real absence, not an unopened panel.
    expect(await within(people).findByText('Justification')).toBeTruthy();
    expect(screen.queryByText('Copilot Credits')).toBeNull();
    expect(screen.queryByText('All Copilot Credits')).toBeNull();
  });

  /**
   * The defect this guards.
   *
   * The justification was a column, clamped to two lines AND off the right-hand edge of a table
   * wider than the screen, so the one sentence written per user to be read could not be read. It
   * now lives in the expander, in full.
   */
  it('keeps the justification out of the row and shows it in full when expanded', async () => {
    const user = userEvent.setup();
    const LONG =
      'Prime candidate - high coordination load (80) with enough Copilot fluency (70) to delegate; '
      + '6 meetings and 80 emails per active day, across 40 Teams messages.';
    fetchCowork.mockResolvedValue(page([row({ rationale: LONG })]));

    render(summary());
    const people = await openSection(user, /People to enable/);
    await waitFor(() => expect(within(people).getByText('aisha.rahman@contoso.com')).toBeTruthy());

    expect(screen.queryByText(LONG)).toBeNull();

    await user.click(within(people).getByRole('button', { name: /Show the full assessment/ }));

    const rationale = await within(people).findByText(LONG);
    expect(rationale).toBeTruthy();
    // Not clamped and not hidden on hover - that combination is what made it unreadable before.
    expect(rationale.getAttribute('title')).toBeNull();
  });

  /**
   * Microsoft reports the scheduled and user-initiated task counts independently, and either can be
   * absent. A blank one means "not reported", not "none" - the same distinction the per-user credit
   * column protects. Rendering it as "0 scheduled" would state a measurement nobody made.
   */
  it('never turns an unreported task split into zeroes', async () => {
    const user = userEvent.setup();
    fetchCowork.mockResolvedValue(
      page([
        row({
          usedCowork: true,
          coworkReportTotalTasks: 40,
          coworkReportScheduledTasks: null,
          coworkReportUserInitiatedTasks: null,
        }),
      ]),
    );

    render(summary());
    const people = await openSection(user, /People to enable/);
    await waitFor(() => expect(within(people).getByText('aisha.rahman@contoso.com')).toBeTruthy());

    await user.click(within(people).getByRole('button', { name: /Show the full assessment/ }));

    expect(await within(people).findByText('split not reported')).toBeTruthy();
    expect(screen.queryByText(/0 scheduled/)).toBeNull();
    expect(screen.queryByText(/0 user-initiated/)).toBeNull();
  });

  it('shows an unattributable credit as a dash, never as zero', async () => {
    const user = userEvent.setup();
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

    const people = await openSection(user, /People to enable/);
    await waitFor(() => expect(within(people).getByText('aisha.rahman@contoso.com')).toBeTruthy());

    await user.click(within(people).getByRole('button', { name: /Show the full assessment/ }));

    // A zero here would read as "this person costs nothing", which is a different claim entirely.
    const creditStat = (await within(people).findByText('All Copilot Credits')).closest('div');
    expect(creditStat).not.toBeNull();
    expect(within(creditStat as HTMLElement).getByText('\u2014')).toBeTruthy();
    expect(
      within(creditStat as HTMLElement).getByText('not attributable to this person - not zero'),
    ).toBeTruthy();
  });

  it('leads with the people ready now, and shows every seat holder as the ceiling', async () => {
    await renderSettled(withEstimates());

    const hero = screen.getByRole('region', { name: '87\u2013173 hours a month' });
    expect(within(hero).getByText('from the 40 people ready for Cowork now')).toBeTruthy();
    expect(within(hero).getByText('933\u20131,865 h')).toBeTruthy();
    expect(within(hero).getByText('a month if all 400 Copilot seat holders used Cowork')).toBeTruthy();
    expect(within(hero).getByText(/^The ceiling, not a target/)).toBeTruthy();
    expect(
      within(hero).getByText(
        '1,732 tasks a month handed to Cowork, on top of what Copilot already saves - the part paid for in Copilot Credits.',
      ),
    ).toBeTruthy();
  });

  /**
   * The point of the activity model. The headline used to be tasks a month x minutes a task - it
   * could say how much time, but not where it would come from. Now it says which work: the meetings
   * people organise and attend, the email they send, their Teams messages and documents.
   */
  it('shows where the time would come from, kind of work by kind of work', async () => {
    await renderSettled(withEstimates());

    const hero = screen.getByRole('region', { name: '87\u2013173 hours a month' });
    expect(within(hero).getByText('Where the time would come from')).toBeTruthy();
    // 4,320 of the 10,392 minutes are email: 72 hours, 42% of the total.
    expect(within(hero).getByText('Send email - 72 h (42%)')).toBeTruthy();
    expect(within(hero).getByText('Organise meetings - 18 h (10%)')).toBeTruthy();
    expect(within(hero).getByText('Prepare for meetings - 29 h (17%)')).toBeTruthy();
    expect(within(hero).getByText('Post in Teams - 36 h (21%)')).toBeTruthy();
    expect(within(hero).getByText('Create documents - 14 h (8%)')).toBeTruthy();
    expect(within(hero).getByText('Cowork tasks already running - 4 h (2%)')).toBeTruthy();
    expect(
      within(hero).getByRole('img', {
        name: 'Modelled time by kind of work: Organise meetings 10%, Prepare for meetings 17%, Send email 42%, Post in Teams 21%, Create documents 8%, Cowork tasks already running 2%',
      }),
    ).toBeTruthy();
  });

  it('models each kind of work from what people already do, with its own share and minutes', async () => {
    await renderSettled(withEstimates());

    const row = screen.getByText('Organise meetings', { selector: 'span' }).closest('tr') as HTMLElement;
    expect(within(row).getByText('Meetings organised in Teams')).toBeTruthy();
    expect(within(row).getByText('720')).toBeTruthy();
    expect(within(row).getByText('180 a month handed to Cowork')).toBeTruthy();
    expect(within(row).getByRole('spinbutton', { name: 'Share of meetings organised handed to Cowork, in percent' })).toHaveValue(25);
    expect(within(row).getByRole('spinbutton', { name: 'Minutes saved on each meeting Cowork organises' })).toHaveValue(6);
    expect(within(row).getByText('18 h')).toBeTruthy();

    // The people already running Cowork tasks are counted, not modelled from their work.
    const observed = screen.getByText('Cowork tasks already running', { selector: 'span' }).closest('tr') as HTMLElement;
    expect(within(observed).getByText('From Microsoft\u2019s Cowork usage report, for the 4 people already running them')).toBeTruthy();
    expect(within(observed).getByText('counted as reported')).toBeTruthy();
    expect(within(observed).getByRole('spinbutton', { name: 'Minutes saved per Cowork task' })).toHaveValue(6);

    expect(
      screen.getByText('Work done by hand a month by the 36 people not yet running Cowork tasks, from Microsoft\u2019s usage reports.'),
    ).toBeTruthy();
  });

  it('badges the headline as modelled and as an assumption, never as published evidence', async () => {
    await renderSettled(withEstimates());

    const hero = screen.getByRole('region', { name: '87\u2013173 hours a month' });
    expect(within(hero).getByText('Modelled, not measured')).toBeTruthy();
    expect(within(hero).getByText('Assumption - not yet studied')).toBeTruthy();
    // Copilot's published studies stand behind the licence estimate. None of them measured Cowork.
    expect(screen.queryByText('Backed by published studies')).toBeNull();
  });

  it('restates the headline as minutes a working day for each person ready now', async () => {
    await renderSettled(withEstimates());

    // 10,392 minutes over 40 people x 20 working days = 13 minutes a day each, 6.5 at the conservative end.
    const hero = screen.getByRole('region', { name: '87\u2013173 hours a month' });
    expect(within(hero).getByText('6.5\u201313 minutes')).toBeTruthy();
    expect(within(hero).getByText('a working day, for each person ready now')).toBeTruthy();
  });

  it('falls back to the ceiling, and says it is one, when nobody is ready for Cowork yet', async () => {
    await renderSettled(
      withEstimates({
        coworkRecommendedForPolicy: 0,
        coworkValueEstimate: {
          ...ESTIMATE,
          cohortUsers: 0,
          coworkTaskUsers: 0,
          observedCoworkTasks: 0,
          projectedCoworkUsers: 0,
          activities: [],
          projectedCoworkTasks: 0,
          coworkTasks: 0,
          hoursPerMonthLow: 0,
          hoursPerMonthHigh: 0,
        },
      }),
    );

    const hero = screen.getByRole('region', { name: '933\u20131,865 hours a month' });
    expect(within(hero).getByText('if all 400 Copilot seat holders used Cowork')).toBeTruthy();
    expect(within(hero).getByText('a working day, for each Copilot seat holder')).toBeTruthy();
    // No second ceiling beside a headline that already is one, and no list of nobody to open.
    expect(within(hero).queryByText(/^a month if all/)).toBeNull();
    expect(within(hero).queryByRole('button', { name: /to enable/ })).toBeNull();
  });

  it('shows the Cowork use already observed, badged as observed', async () => {
    await renderSettled(withEstimates({ coworkDetected: true, coworkUsers: 4, coworkReportTotalTasks: 40 }));

    const hero = screen.getByRole('region', { name: '87\u2013173 hours a month' });
    expect(within(hero).getByText('Observed')).toBeTruthy();
    expect(within(hero).getByText('Already happening: 4 people used Cowork in this period, running 40 tasks.')).toBeTruthy();
  });

  /**
   * The defect this guards. The headline used to be one blended "Copilot and Cowork together" figure,
   * most of it Microsoft 365 Copilot's minutes on meetings, email and documents - for people who
   * already hold a licence. Enabling Cowork unlocks none of that, so the number quoted to justify
   * Copilot Credits was mostly the licence's, and rested on evidence that never measured Cowork. The
   * Cowork model reads meetings, email and documents too now - but only as work Cowork could take on,
   * at Cowork's own shares and minutes.
   */
  it('has no Microsoft 365 Copilot figure: that time belongs to the licence these people already hold', async () => {
    await renderSettled(withEstimates());

    expect(screen.queryByRole('spinbutton', { name: 'Minutes saved per meeting' })).toBeNull();
    expect(screen.queryByRole('spinbutton', { name: 'Minutes saved per email' })).toBeNull();
    expect(screen.queryByRole('spinbutton', { name: 'Minutes saved per document' })).toBeNull();
    expect(screen.queryByText(/^Assumes Microsoft 365 Copilot saves/)).toBeNull();
    expect(screen.queryByText('Sense check against published studies')).toBeNull();
    expect(screen.queryByText(/^Meetings: \d+ min each$/)).toBeNull();
    expect(document.body.textContent).not.toMatch(/Copilot and Cowork together|used Copilot and Cowork fully/);
  });

  it('renders catalogued estimate assumptions from the facts in force', async () => {
    await renderSettled(withEstimates());

    expect(
      screen.getByText(
        "Assumes Cowork saves 6 minutes on each meeting it organises, 6 on each meeting it prepares someone for, 6 on each email it sends, 6 on each Teams message it posts, 6 on each document it creates and 6 on each Cowork task already in Microsoft's report, on top of what Microsoft 365 Copilot already saves - Cowork's increment over Copilot alone. No study has yet measured Cowork's time savings, alone or for people who already use Copilot, so these figures are assumptions.",
      ),
    ).toBeTruthy();
    expect(
      screen.getByText(
        'Assumes people hand Cowork 25% of the meetings they organise, 10% of the meetings they attend, 5% of the emails they send, 1% of their Teams messages and 2% of the files they work on. No study has measured how much work people hand to Cowork either, so these shares are assumptions too.',
      ),
    ).toBeTruthy();
    expect(
      screen.getByText(
        "Covers 40 Copilot seat holders. The work people already do comes from Microsoft's usage reports, restated over 20 working days a month; Cowork tasks from Microsoft's Cowork usage report are restated as a 28-day month.",
      ),
    ).toBeTruthy();
    expect(
      screen.getByText(
        "Cowork tasks already in Microsoft's Cowork usage report are counted as reported: 40 a month from the 4 people running them. Only everyone else's work is modelled, so nobody is counted twice.",
      ),
    ).toBeTruthy();
    expect(screen.getByText(/^The minutes are Cowork's increment over Copilot alone\./)).toBeTruthy();
    expect(screen.getByText(/^Only work Microsoft's usage reports count is modelled\./)).toBeTruthy();
    expect(
      screen.getByText('The lower bound applies 50% of the minutes saved; the upper bound applies them in full.'),
    ).toBeTruthy();
    expect(screen.getByText(/potential at full use, not the gain over today/)).toBeTruthy();
    expect(screen.getByText(/These figures are a model for sizing a Cowork rollout, not a result\./)).toBeTruthy();
    expect(screen.getByText(/This report reports seats, people and hours - never money\./)).toBeTruthy();
  });

  it('shows the working for either cohort, starting with the people ready now', async () => {
    const user = userEvent.setup();
    await renderSettled(withEstimates());

    expect(screen.getByRole('radio', { name: 'The 40 people ready now' })).toBeChecked();
    await user.click(screen.getByRole('radio', { name: 'All 400 Copilot seat holders' }));

    expect(
      screen.getByText(
        "Covers 400 Copilot seat holders. The work people already do comes from Microsoft's usage reports, restated over 20 working days a month; Cowork tasks from Microsoft's Cowork usage report are restated as a 28-day month.",
      ),
    ).toBeTruthy();
    // 14,400 emails x 11 for the 396 people modelled.
    expect(screen.getByText('158,400')).toBeTruthy();
    // The calculator shows its working for another cohort; the headline stays on the decision.
    expect(screen.getByRole('region', { name: '87\u2013173 hours a month' })).toBeTruthy();
  });

  it('never converts the modelled hours into money', async () => {
    await renderSettled(withEstimates());

    // Hours are the whole output. A currency figure derived from a modelled number is how a model
    // ends up quoted as a saving, which is exactly what this estimate must not become.
    expect(screen.getByText('87\u2013173 hours a month')).toBeTruthy();
    expect(screen.queryByText(/at the configured loaded hourly cost/)).toBeNull();
    expect(document.body.textContent).not.toMatch(/[£$€]\s?\d/);
  });

  it('shows no headline for an empty cohort', async () => {
    render(summary());

    expect(screen.queryByText(/^[\d,]+\u2013[\d,]+ hours a month$/)).toBeNull();
    expect(screen.queryByText('Modelled, not measured')).toBeNull();
  });

  it('says the usage reports are missing, rather than that there is nothing to model, when they have not been imported', async () => {
    await renderSettled(
      summary({
        dataSources: { m365UsageReportsAvailable: false } as CopilotAdoptionSummary['dataSources'],
      }),
    );

    expect(screen.getByText(/None have been imported for this period, so there is nothing to model yet\./)).toBeTruthy();
    expect(screen.queryByText(/^There is nobody to model yet/)).toBeNull();
  });
});

describe('CoworkPanel time-saved assumptions', () => {
  beforeEach(() => {
    fetchCowork.mockReset();
    fetchCowork.mockResolvedValue(page([row({})]));
    resetTimeSavedStore();
  });

  it('recomputes the headline from the reader\u2019s own minutes and keeps them for the session', async () => {
    const user = userEvent.setup();
    await renderSettled(withEstimates());

    const input = screen.getByRole('spinbutton', { name: 'Minutes saved on each email Cowork sends' });
    await user.clear(input);
    await user.type(input, '12');

    // 720 emails handed over x 6 more minutes = 4,320 more: 14,712 minutes = 245.2 hours, 122.6
    // conservative. The ceiling's 7,920 emails give 159,432 minutes = 2,657.2 hours.
    expect(await screen.findByText('123\u2013245 hours a month')).toBeTruthy();
    expect(screen.getByText('1,329\u20132,657 h')).toBeTruthy();
    expect(screen.getByText(/^Using your figures for how much of each kind of work Cowork takes on/)).toBeTruthy();
    expect(JSON.parse(sessionStorage.getItem(TIME_SAVED_STORAGE_KEY) ?? '{}')).toEqual({ sendEmailMinutes: 12 });

    await user.click(screen.getByRole('button', { name: 'Reset every figure to the product default' }));
    expect(await screen.findByText('87\u2013173 hours a month')).toBeTruthy();
    expect(sessionStorage.getItem(TIME_SAVED_STORAGE_KEY)).toBeNull();
  });

  it('recomputes from the reader\u2019s own share, and states it with the assumptions', async () => {
    const user = userEvent.setup();
    await renderSettled(withEstimates());

    const input = screen.getByRole('spinbutton', { name: 'Share of emails sent handed to Cowork, in percent' });
    await user.clear(input);
    await user.type(input, '10');

    // Twice the emails handed over: the same 4,320 more minutes as doubling their minutes.
    expect(await screen.findByText('123\u2013245 hours a month')).toBeTruthy();
    expect(screen.getByText(/, 10% of the emails they send,/)).toBeTruthy();
    expect(screen.getByText('1,440 a month handed to Cowork')).toBeTruthy();
    expect(JSON.parse(sessionStorage.getItem(TIME_SAVED_STORAGE_KEY) ?? '{}')).toEqual({ sendEmailShare: 0.1 });
  });

  it('refuses a figure outside the bounds rather than modelling it', async () => {
    const user = userEvent.setup();
    await renderSettled(withEstimates());

    // Typed a character at a time, so the figure must be invalid at every step.
    const input = screen.getByRole('spinbutton', { name: 'Minutes saved on each email Cowork sends' });
    await user.clear(input);
    await user.type(input, '-3');

    expect(await screen.findByRole('alert')).toHaveTextContent('Enter a number from 0 to 240.');
    expect(screen.getByText('87\u2013173 hours a month')).toBeTruthy();
    expect(sessionStorage.getItem(TIME_SAVED_STORAGE_KEY)).toBeNull();
  });

  it('refuses a share above all of the work', async () => {
    const user = userEvent.setup();
    await renderSettled(withEstimates());

    // Pasted whole: typed a character at a time, '15' on the way to '150' is a valid share of its own.
    const input = screen.getByRole('spinbutton', { name: 'Share of Teams messages handed to Cowork, in percent' });
    await user.clear(input);
    await user.paste('150');

    expect(await screen.findByRole('alert')).toHaveTextContent('Enter a number from 0 to 100.');
    expect(sessionStorage.getItem(TIME_SAVED_STORAGE_KEY)).toBeNull();
  });

  it('says so plainly when nobody here runs Cowork tasks yet, and models everyone from their work', async () => {
    await renderSettled(
      withEstimates({
        coworkValueEstimate: {
          ...ESTIMATE,
          coworkTaskUsers: 0,
          observedCoworkTasks: 0,
          projectedCoworkUsers: 40,
          coworkTasks: 1692,
          observedTasksPerPersonPerMonth: 0,
          observedTaskRateUsers: 0,
          // 1,692 pieces x 6 = 10,152 minutes = 169.2 hours.
          hoursPerMonthLow: 85,
          hoursPerMonthHigh: 169,
        },
        coworkFullRolloutEstimate: undefined,
      }),
    );

    expect(screen.getByText('85\u2013169 hours a month')).toBeTruthy();
    expect(
      screen.getByText(
        "Nobody here has Cowork tasks in Microsoft's Cowork usage report yet, so everyone is modelled from the work they already do.",
      ),
    ).toBeTruthy();
    expect(screen.queryByText('Cowork tasks already running', { selector: 'span' })).toBeNull();
    expect(screen.getByText(/^No Cowork tasks are in Microsoft\u2019s Cowork usage report yet, so there is nothing of your own/)).toBeTruthy();
  });

  it('holds the model against the tenant\u2019s own Cowork users', async () => {
    await renderSettled(withEstimates());

    // 1,692 pieces over the 36 people modelled = 47 a month each.
    expect(
      screen.getByText(
        'Your 4 Cowork users run 10 tasks a month each, from Microsoft\u2019s Cowork usage report. This model hands each person not yet running Cowork 47 pieces of work a month.',
      ),
    ).toBeTruthy();
    expect(screen.getByText(/^Early adopters tend to use a new tool more than the people who follow/)).toBeTruthy();
  });

  it('explains every default share as the judgement it is', async () => {
    await renderSettled(withEstimates());

    expect(screen.getByText('How much of each kind of work Cowork takes on')).toBeTruthy();
    expect(screen.getByText(/^25% of the meetings people organise - one in four\./)).toBeTruthy();
    expect(screen.getByText(/^5% of the emails they send - one in twenty\./)).toBeTruthy();
    expect(screen.getByText(/^1% of their Teams messages - one in a hundred\./)).toBeTruthy();
  });

  it('says no study has measured Cowork, and cites its nearest evidence as only that', async () => {
    await renderSettled(withEstimates());

    expect(
      screen.getByText(/^No published study has measured Cowork\u2019s time savings - alone, or on top of Microsoft 365 Copilot\./),
    ).toBeTruthy();
    expect(screen.getByText('Nearest published evidence - none of it measured Cowork')).toBeTruthy();
    const links = screen
      .getAllByRole('link', { hidden: true })
      .map((a) => a.getAttribute('href') ?? '')
      .filter((href) => href.startsWith('https://'));
    expect(links.length).toBeGreaterThanOrEqual(3);
    expect(screen.getAllByText('Vendor model').length).toBeGreaterThan(0);
    // The Copilot studies are the licence estimate's evidence, on the Licence opportunities tab.
    expect(screen.queryByText(/no statistically significant change in how long documents took/)).toBeNull();
  });

  it('says which of its figures the licence estimate shares', async () => {
    await renderSettled(withEstimates());

    expect(
      screen.getByText(/^The conservative share and the hours in a working day apply to both time-saved estimates/),
    ).toBeTruthy();
  });
});

describe('CoworkPanel headline actions', () => {
  const scrollIntoView = vi.fn();
  const originalScrollIntoView = Element.prototype.scrollIntoView;

  beforeEach(() => {
    fetchCowork.mockReset();
    fetchCowork.mockResolvedValue(page([row({})]));
    resetTimeSavedStore();
    scrollIntoView.mockReset();
    // jsdom implements no scrolling, so the call is recorded rather than performed.
    Element.prototype.scrollIntoView = scrollIntoView;
  });

  afterEach(() => {
    Element.prototype.scrollIntoView = originalScrollIntoView;
  });

  /**
   * The defect this guards. The tab opens on the Time saved section, so "Adjust the assumptions"
   * re-selected the section already showing - and with the table below the headline, nothing on
   * screen changed and the button read as broken.
   */
  it('takes the reader to the figures even when the Time saved section is already open', async () => {
    const user = userEvent.setup();
    await renderSettled(withEstimates());
    expect(screen.getByRole('tab', { name: /Time saved/, selected: true })).toBeTruthy();

    await user.click(screen.getByRole('button', { name: 'Adjust the assumptions' }));

    expect(scrollIntoView).toHaveBeenCalled();
    expect(screen.getByRole('spinbutton', { name: 'Share of meetings organised handed to Cowork, in percent' })).toHaveFocus();
  });

  it('opens the Time saved section from any other section to do it', async () => {
    const user = userEvent.setup();
    await renderSettled(withEstimates());
    await openSection(user, /People to enable/);
    expect(screen.queryByRole('spinbutton', { name: 'Share of meetings organised handed to Cowork, in percent' })).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Adjust the assumptions' }));

    expect(screen.getByRole('tab', { name: /Time saved/, selected: true })).toBeTruthy();
    expect(screen.getByRole('spinbutton', { name: 'Share of meetings organised handed to Cowork, in percent' })).toHaveFocus();
  });

  it('opens the policy list, scrolled into view, from the headline', async () => {
    const user = userEvent.setup();
    await renderSettled(withEstimates());
    await waitFor(() => expect(fetchCowork).toHaveBeenCalled());
    fetchCowork.mockClear();

    await user.click(screen.getByRole('button', { name: 'See the 40 people to enable' }));

    expect(screen.getByRole('tab', { name: /People to enable/, selected: true })).toBeTruthy();
    expect(scrollIntoView).toHaveBeenCalled();
    await waitFor(() => expect(fetchCowork).toHaveBeenCalled());
    expect((fetchCowork.mock.calls.at(-1)![1] as { recommendedOnly: boolean }).recommendedOnly).toBe(true);
  });
});

describe('CoworkPanel email-domain scope', () => {
  beforeEach(() => {
    fetchCowork.mockReset();
    fetchCowork.mockResolvedValue(page([]));
  });

  it('asks the server for the domain the page is narrowed to', async () => {
    renderWithProvider(
      <CoworkPanel
        windowDays={28}
        summary={summary({})}
        filterOptions={null}
        options={summary({}).options}
        emailDomain="fabrikam.com"
      />,
    );

    await waitFor(() => expect(fetchCowork).toHaveBeenCalled());

    const call = fetchCowork.mock.calls[0];
    expect(call[0]).toBe(28);
    expect((call[1] as { emailDomain: string }).emailDomain).toBe('fabrikam.com');
  });

  it('keeps the page-wide domain when the panel filters are cleared', async () => {
    // The domain is not one of this panel's filters - it is the population the whole report is
    // describing. Clearing it here would silently widen the list (and the spending-policy CSV built
    // from the same state) back to the whole tenant while the page still named one organisation.
    const user = userEvent.setup();

    renderWithProvider(
      <CoworkPanel
        windowDays={28}
        summary={summary({})}
        filterOptions={null}
        options={summary({}).options}
        emailDomain="fabrikam.com"
      />,
    );

    await screen.findByText('No Copilot seat holders match these filters.');
    await user.click(screen.getByRole('tab', { name: /People to enable/ }));
    fetchCowork.mockClear();

    await user.click(screen.getByRole('button', { name: 'Clear filters' }));

    await waitFor(() => expect(fetchCowork).toHaveBeenCalled());
    for (const call of fetchCowork.mock.calls) {
      expect((call[1] as { emailDomain: string }).emailDomain).toBe('fabrikam.com');
    }
  });
});

/**
 * The people list on paper.
 *
 * A printed page cannot be clicked, so three things the screen does with controls have to be done
 * before the printout exists: every row of the list is on it rather than one page of fifty, each
 * row's full assessment is on it when the reader asked for it with "Expand all", and the filter bar
 * - which is not printed - is replaced by a line saying what it was set to.
 */
// Rendering a hundred-odd rows through Fluent in jsdom takes seconds, not milliseconds, and a CI
// runner is slower still - so these carry a longer timeout than the suite's default.
describe('CoworkPanel printing', { timeout: 30000 }, () => {
  const upn = (id: number) => `demo.user${String(id).padStart(7, '0')}@contoso.example`;

  /** A server holding `total` seat holders, paging and clamping exactly as the API does. */
  function serve(total: number) {
    fetchCowork.mockImplementation(async (_windowDays: number, _filters: unknown, skip: number, take: number) => {
      const count = Math.max(0, Math.min(take, 500, total - skip));
      return {
        total,
        skip,
        take,
        warnings: [],
        rows: Array.from({ length: count }, (_, i) => row({ userId: skip + i + 1, userPrincipalName: upn(skip + i + 1) })),
      };
    });
  }

  const listed = (people: HTMLElement) => within(people).queryAllByText(/^demo\.user\d+@contoso\.example$/).length;

  async function openPeople(total: number) {
    serve(total);
    const user = userEvent.setup();
    render(summary());
    const people = await openSection(user, /People to enable/);
    await waitFor(() => expect(listed(people)).toBe(Math.min(total, 50)));
    return { user, people };
  }

  /** What the browser would have captured: the document as it stood when window.print ran. */
  function capturePrint(people: HTMLElement) {
    const captured = { rows: -1, details: -1, footer: '' };
    vi.spyOn(window, 'print').mockImplementation(() => {
      captured.rows = listed(people);
      captured.details = within(people).queryAllByText('Justification').length;
      captured.footer = within(people).getByText(/^Showing /).textContent ?? '';
    });
    return captured;
  }

  beforeEach(() => {
    fetchCowork.mockReset();
    resetTimeSavedStore();
    resetPrintPreparation();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    resetPrintPreparation();
  });

  it('prints every seat holder rather than the page on screen, then returns to that page', async () => {
    const { people } = await openPeople(120);
    const printed = capturePrint(people);

    await act(async () => {
      await expect(requestPrint()).resolves.toEqual({ kind: 'printed' });
    });

    expect(printed.rows).toBe(120);
    expect(printed.footer).toBe('Showing 1-120 of 120 seat holders');
    expect(listed(people)).toBe(50);
    // Loaded with the list's own filters and sort, so the printout is the list on screen - all of it.
    const last = fetchCowork.mock.calls.at(-1)!;
    expect([last[2], last[3]]).toEqual([0, 120]);
    expect(last[1]).toBe(fetchCowork.mock.calls[0][1]);
  });

  it('prints every row open when every row was expanded - including the ones on later pages', async () => {
    // Sixty rows: one full page of fifty on screen, and ten more that only the printout loads.
    const { user, people } = await openPeople(60);
    await user.click(within(people).getByRole('button', { name: 'Expand all' }));
    expect(within(people).getAllByText('Justification')).toHaveLength(50);

    const printed = capturePrint(people);
    await act(async () => {
      await requestPrint();
    });

    expect(printed.details).toBe(60);
  });

  it('prints rows collapsed unless the reader opened them', async () => {
    const { people } = await openPeople(120);
    const printed = capturePrint(people);

    await act(async () => {
      await requestPrint();
    });

    expect(printed.details).toBe(0);
  });

  it('keeps expand-all when the page is turned', async () => {
    const { user, people } = await openPeople(60);
    await user.click(within(people).getByRole('button', { name: 'Expand all' }));

    await user.click(within(people).getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(within(people).getByText(upn(51))).toBeTruthy());

    expect(within(people).getAllByText('Justification')).toHaveLength(10);
    expect(within(people).getByRole('button', { name: 'Collapse all' })).toBeTruthy();
  });

  it('refuses, rather than printing one page, when the list is longer than can be printed', async () => {
    const { people } = await openPeople(PRINT_ROW_LIMIT + 1);
    const calls = fetchCowork.mock.calls.length;
    capturePrint(people);

    await expect(requestPrint()).resolves.toMatchObject({ kind: 'tooManyRows', rows: PRINT_ROW_LIMIT + 1 });

    expect(window.print).not.toHaveBeenCalled();
    expect(fetchCowork.mock.calls.length).toBe(calls);
  });

  it('does not hold up a print of another section, however long the list', async () => {
    // The tab opens on the time-saved section. The list is not on that printout, so it must not
    // be loaded for it - or refuse it for being long.
    serve(PRINT_ROW_LIMIT * 5);
    render(summary());
    await waitFor(() => expect(fetchCowork).toHaveBeenCalled());
    const calls = fetchCowork.mock.calls.length;
    vi.spyOn(window, 'print').mockImplementation(() => {});

    await expect(requestPrint()).resolves.toEqual({ kind: 'printed' });
    expect(fetchCowork.mock.calls.length).toBe(calls);
  });

  it('keeps the filter bar, the pager and every expander off paper', async () => {
    const { people } = await openPeople(120);
    const onPaper = (element: Element) => element.closest('[data-print="hide"]') === null;

    for (const control of within(people).getAllByRole('combobox')) expect(onPaper(control)).toBe(false);
    for (const control of within(people).getAllByRole('checkbox')) expect(onPaper(control)).toBe(false);
    for (const name of ['Search', 'Expand all', 'Refresh', 'Previous', 'Next']) {
      expect(onPaper(within(people).getByRole('button', { name }))).toBe(false);
    }
    expect(onPaper(within(people).getByText('Export CSV'))).toBe(false);
    for (const expander of within(people).getAllByRole('button', { name: /Show the full assessment/ })) {
      expect(onPaper(expander)).toBe(false);
    }
    // The count stays: a printout should still say how many people it lists.
    expect(onPaper(within(people).getByText(/^Showing 1-50 of 120/))).toBe(true);
  });

  it('prints what the filter bar was set to, in its place', async () => {
    const { user, people } = await openPeople(3);
    const printedFilters = () => people.querySelector('[data-print="only"]')?.textContent;

    expect(printedFilters()).toBe(
      'Filters: Verdict: All verdicts \u00b7 Department: All departments \u00b7 Sorted by: Most coordination load',
    );

    await user.selectOptions(
      within(people).getByRole('combobox', { name: 'Filter Cowork candidates by verdict' }),
      'primeCandidate',
    );
    await user.click(within(people).getByRole('checkbox', { name: 'Already using Cowork' }));
    await user.type(within(people).getByRole('textbox', { name: 'Search Cowork candidates' }), 'finance{Enter}');

    await waitFor(() =>
      expect(printedFilters()).toBe(
        'Filters: Search: \u201cfinance\u201d \u00b7 Verdict: Prime candidate \u00b7 Department: All departments'
          + ' \u00b7 Sorted by: Most coordination load \u00b7 Already using Cowork',
      ),
    );
  });

  it('says on paper when only the page on screen was printed', async () => {
    // A print from the browser's own menu cannot wait for the rest of the list to load, so it
    // carries the fifty rows on screen - and must not pass them off as the whole list.
    const { people } = await openPeople(120);

    const note = [...people.querySelectorAll('[data-print="only"]')].find((n) =>
      n.textContent?.includes('only the rows that were on screen'),
    );
    expect(note).toBeTruthy();
  });
});
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
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
  coworkAssumedTasksPerPersonPerMonth: 20,
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
      coworkTasksPerPersonPerMonth: 20,
      coworkTaskRateBasis: 'assumed',
      coworkTaskRateUsers: 0,
      coworkTasks: 0,
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

// The people ready for Cowork now. 40 tasks already in Microsoft's report from 4 people, plus 36
// people projected at those 4 people's average of 10 = 400 tasks x 6 minutes = 2,400 minutes =
// 40 hours, 20 at the 50% conservative end.
const ESTIMATE = {
  isModelled: true,
  cohortUsers: 40,
  coworkTaskUsers: 4,
  observedCoworkTasks: 40,
  projectedCoworkUsers: 36,
  coworkTasksPerPersonPerMonth: 10,
  coworkTaskRateBasis: 'observed' as const,
  coworkTaskRateUsers: 4,
  coworkTasks: 400,
  hoursPerMonthLow: 20,
  hoursPerMonthHigh: 40,
  assumptions: ['Assumes each Cowork task saves 6 minutes.', 'Time saved is NOT measured by this product.'],
};

// Every Copilot seat holder, the ceiling: the same 4 observed people, plus 396 projected at 10 =
// 4,000 tasks x 6 minutes = 24,000 minutes = 400 hours, 200 conservative.
const FULL_ESTIMATE = {
  ...ESTIMATE,
  cohortUsers: 400,
  projectedCoworkUsers: 396,
  coworkTasks: 4000,
  hoursPerMonthLow: 200,
  hoursPerMonthHigh: 400,
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

    const hero = screen.getByRole('region', { name: '20\u201340 hours a month' });
    expect(within(hero).getByText('from the 40 people ready for Cowork now')).toBeTruthy();
    expect(within(hero).getByText('200\u2013400 h')).toBeTruthy();
    expect(within(hero).getByText('a month if all 400 Copilot seat holders used Cowork')).toBeTruthy();
    expect(within(hero).getByText(/^The ceiling, not a target/)).toBeTruthy();
    expect(
      within(hero).getByText(
        '400 Cowork tasks a month at 6 min each, on top of what Copilot already saves - the part paid for in Copilot Credits.',
      ),
    ).toBeTruthy();
  });

  it('badges the headline as modelled and as an assumption, never as published evidence', async () => {
    await renderSettled(withEstimates());

    const hero = screen.getByRole('region', { name: '20\u201340 hours a month' });
    expect(within(hero).getByText('Modelled, not measured')).toBeTruthy();
    expect(within(hero).getByText('Assumption - not yet studied')).toBeTruthy();
    // Copilot's published studies stand behind the licence estimate. None of them measured Cowork.
    expect(screen.queryByText('Backed by published studies')).toBeNull();
  });

  it('restates the headline as minutes a working day for each person ready now', async () => {
    await renderSettled(withEstimates());

    // 2,400 minutes over 40 people x 20 working days = 3 minutes a day each, 1.5 at the conservative end.
    const hero = screen.getByRole('region', { name: '20\u201340 hours a month' });
    expect(within(hero).getByText('1.5\u20133 minutes')).toBeTruthy();
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
          coworkTasks: 0,
          hoursPerMonthLow: 0,
          hoursPerMonthHigh: 0,
        },
      }),
    );

    const hero = screen.getByRole('region', { name: '200\u2013400 hours a month' });
    expect(within(hero).getByText('if all 400 Copilot seat holders used Cowork')).toBeTruthy();
    expect(within(hero).getByText('a working day, for each Copilot seat holder')).toBeTruthy();
    // No second ceiling beside a headline that already is one, and no list of nobody to open.
    expect(within(hero).queryByText(/^a month if all/)).toBeNull();
    expect(within(hero).queryByRole('button', { name: /to enable/ })).toBeNull();
  });

  it('shows the Cowork use already observed, badged as observed', async () => {
    await renderSettled(withEstimates({ coworkDetected: true, coworkUsers: 4, coworkReportTotalTasks: 40 }));

    const hero = screen.getByRole('region', { name: '20\u201340 hours a month' });
    expect(within(hero).getByText('Observed')).toBeTruthy();
    expect(within(hero).getByText('Already happening: 4 people used Cowork in this period, running 40 tasks.')).toBeTruthy();
  });

  /**
   * The defect this guards. The headline used to be one blended "Copilot and Cowork together" figure,
   * most of it Microsoft 365 Copilot's minutes on meetings, email and documents - for people who
   * already hold a licence. Enabling Cowork unlocks none of that, so the number quoted to justify
   * Copilot Credits was mostly the licence's, and rested on evidence that never measured Cowork.
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
      screen.getByText(/^Assumes each Cowork task saves 6 minutes on top of what Microsoft 365 Copilot already saves/),
    ).toBeTruthy();
    expect(
      screen.getByText(
        "Covers 40 Copilot seat holders. Cowork tasks from Microsoft's Cowork usage report are restated as a 28-day month.",
      ),
    ).toBeTruthy();
    expect(
      screen.getByText(/Everyone else is projected at 10 tasks a month each: the average of the 4 people already running them/),
    ).toBeTruthy();
    expect(screen.getByText(/^The minutes are Cowork's increment over Copilot alone\./)).toBeTruthy();
    expect(
      screen.getByText('The lower bound applies 50% of the minutes saved per task; the upper bound applies them in full.'),
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
        "Covers 400 Copilot seat holders. Cowork tasks from Microsoft's Cowork usage report are restated as a 28-day month.",
      ),
    ).toBeTruthy();
    expect(screen.getByText('4,000')).toBeTruthy();
    // The calculator shows its working for another cohort; the headline stays on the decision.
    expect(screen.getByRole('region', { name: '20\u201340 hours a month' })).toBeTruthy();
  });

  it('never converts the modelled hours into money', async () => {
    await renderSettled(withEstimates());

    // Hours are the whole output. A currency figure derived from a modelled number is how a model
    // ends up quoted as a saving, which is exactly what this estimate must not become.
    expect(screen.getByText('20\u201340 hours a month')).toBeTruthy();
    expect(screen.queryByText(/at the configured loaded hourly cost/)).toBeNull();
    expect(document.body.textContent).not.toMatch(/[£$€]\s?\d/);
  });

  it('shows no headline for an empty cohort', async () => {
    render(summary());

    expect(screen.queryByText(/^[\d,]+\u2013[\d,]+ hours a month$/)).toBeNull();
    expect(screen.queryByText('Modelled, not measured')).toBeNull();
  });
});

describe('CoworkPanel time-saved assumptions', () => {
  beforeEach(() => {
    fetchCowork.mockReset();
    fetchCowork.mockResolvedValue(page([row({})]));
    resetTimeSavedStore();
  });

  it('recomputes the headline from the reader\u2019s own figure and keeps it for the session', async () => {
    const user = userEvent.setup();
    await renderSettled(withEstimates());

    const input = screen.getByRole('spinbutton', { name: 'Minutes saved per Cowork task' });
    await user.clear(input);
    await user.type(input, '12');

    // 400 tasks x 12 minutes = 4,800 minutes = 80 hours; the ceiling's 4,000 tasks give 800.
    expect(await screen.findByText('40\u201380 hours a month')).toBeTruthy();
    expect(screen.getByText('400\u2013800 h')).toBeTruthy();
    expect(screen.getByText(/^Using your figures: 12 min per Cowork task/)).toBeTruthy();
    expect(JSON.parse(sessionStorage.getItem(TIME_SAVED_STORAGE_KEY) ?? '{}')).toEqual({ taskMinutes: 12 });

    await user.click(screen.getByRole('button', { name: 'Reset every figure to the product default' }));
    expect(await screen.findByText('20\u201340 hours a month')).toBeTruthy();
    expect(sessionStorage.getItem(TIME_SAVED_STORAGE_KEY)).toBeNull();
  });

  it('refuses a figure outside the bounds rather than modelling it', async () => {
    const user = userEvent.setup();
    await renderSettled(withEstimates());

    // Typed a character at a time, so the figure must be invalid at every step.
    const input = screen.getByRole('spinbutton', { name: 'Minutes saved per Cowork task' });
    await user.clear(input);
    await user.type(input, '-3');

    expect(await screen.findByRole('alert')).toHaveTextContent('Enter a number from 0 to 240.');
    expect(screen.getByText('20\u201340 hours a month')).toBeTruthy();
    expect(sessionStorage.getItem(TIME_SAVED_STORAGE_KEY)).toBeNull();
  });

  it('recomputes from the reader\u2019s own task rate, and labels it as theirs', async () => {
    const user = userEvent.setup();
    await renderSettled(withEstimates());

    const input = screen.getByRole('spinbutton', { name: 'Cowork tasks a month for each person not yet running them' });
    await user.clear(input);
    await user.type(input, '30');

    // 40 observed + 36 x 30 = 1,120 tasks x 6 = 6,720 minutes = 112 hours, 56 conservative.
    expect(await screen.findByText('56\u2013112 hours a month')).toBeTruthy();
    expect(screen.getByText(/Everyone else is projected at 30 tasks a month each: your own figure\./)).toBeTruthy();
    expect(screen.getByText(/The Cowork task rate is your own figure\.$/)).toBeTruthy();
    expect(JSON.parse(sessionStorage.getItem(TIME_SAVED_STORAGE_KEY) ?? '{}')).toEqual({ tasksPerPerson: 30 });
  });

  it('says so plainly when the task rate is a placeholder because no Cowork use is observed', async () => {
    await renderSettled(
      withEstimates({
        coworkValueEstimate: {
          ...ESTIMATE,
          coworkTaskUsers: 0,
          observedCoworkTasks: 0,
          projectedCoworkUsers: 40,
          coworkTasksPerPersonPerMonth: 20,
          coworkTaskRateBasis: 'assumed' as const,
          coworkTaskRateUsers: 0,
          coworkTasks: 800,
          hoursPerMonthLow: 40,
          hoursPerMonthHigh: 80,
        },
        coworkFullRolloutEstimate: undefined,
      }),
    );

    // 40 people x 20 placeholder tasks x 6 minutes = 4,800 minutes = 80 hours.
    expect(screen.getByText('40\u201380 hours a month')).toBeTruthy();
    expect(
      screen.getByText(/so everyone is projected at 20 tasks a month each: a placeholder until real use is observed\./),
    ).toBeTruthy();
    expect(screen.getByText('placeholder - no Cowork tasks seen yet')).toBeTruthy();
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
    expect(screen.getByRole('spinbutton', { name: 'Minutes saved per Cowork task' })).toHaveFocus();
  });

  it('opens the Time saved section from any other section to do it', async () => {
    const user = userEvent.setup();
    await renderSettled(withEstimates());
    await openSection(user, /People to enable/);
    expect(screen.queryByRole('spinbutton', { name: 'Minutes saved per Cowork task' })).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Adjust the assumptions' }));

    expect(screen.getByRole('tab', { name: /Time saved/, selected: true })).toBeTruthy();
    expect(screen.getByRole('spinbutton', { name: 'Minutes saved per Cowork task' })).toHaveFocus();
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
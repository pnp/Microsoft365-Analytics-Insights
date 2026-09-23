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
  coworkMinutesSavedPerMeeting: 5,
  coworkMinutesSavedPerMailThread: 1,
  coworkMinutesSavedPerDocument: 3,
  coworkEstimateLowerBoundRatio: 0.5,
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

/** The tab is sectioned now; most assertions start by opening the section they are about. */
async function openSection(user: ReturnType<typeof userEvent.setup>, name: RegExp) {
  await user.click(screen.getByRole('tab', { name }));
  return screen.getByRole('tabpanel', { name });
}

const ESTIMATE = {
  isModelled: true,
  cohortUsers: 40,
  addressableMeetings: 4800,
  addressableMailThreads: 64000,
  addressableDocuments: 9600,
  hoursPerMonthLow: 973,
  hoursPerMonthHigh: 1947,
  assumptions: ['Assumes 5 minutes per meeting.', 'Time saved is NOT measured by this product.'],
};

const FULL_ESTIMATE = {
  ...ESTIMATE,
  cohortUsers: 400,
  addressableMeetings: 24000,
  addressableMailThreads: 320000,
  addressableDocuments: 48000,
};

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

  it('renders catalogued estimate assumptions from the facts in force', async () => {
    render(summary({ coworkValueEstimate: ESTIMATE }));

    expect(screen.getAllByText('Modelled, not measured').length).toBeGreaterThan(0);
    expect(
      screen.getByText('Assumes Microsoft 365 Copilot saves 5 minutes per meeting, 1 per email and 3 per document.'),
    ).toBeTruthy();
    expect(
      screen.getByText("The lower bound applies 50% of every assumption, Copilot's and Cowork's; the upper bound applies them in full."),
    ).toBeTruthy();
    expect(
      screen.getByText("Volumes are observed from Microsoft's usage reports for 40 users, restated over 20 working days a month."),
    ).toBeTruthy();
    expect(screen.getByText(/potential at full use, not the gain over today/)).toBeTruthy();
    expect(screen.getByText(/Time saved is NOT measured/)).toBeTruthy();
    expect(screen.getByText(/This report reports seats, people and hours - never money\./)).toBeTruthy();
  });

  it('shows the headline as a range rather than a single number', async () => {
    // 4,800 x 5 + 64,000 x 1 + 9,600 x 3 = 116,800 minutes = 1,947 hours; the conservative end is 50%.
    render(summary({ coworkValueEstimate: ESTIMATE }));

    expect(screen.getByText('973\u20131,947 hours a month')).toBeTruthy();
  });

  it('leads with full adoption and keeps the people ready now beside it', async () => {
    render(summary({ coworkValueEstimate: ESTIMATE, coworkFullRolloutEstimate: FULL_ESTIMATE }));

    // 24,000 x 5 + 320,000 x 1 + 48,000 x 3 = 584,000 minutes = 9,733 hours.
    expect(screen.getByText('4,867\u20139,733 hours a month')).toBeTruthy();
    expect(screen.getByText('if all 400 Copilot seat holders used Copilot and Cowork fully')).toBeTruthy();
    expect(screen.getByText('973\u20131,947 h')).toBeTruthy();
    expect(screen.getByText('a month from the 40 people ready now')).toBeTruthy();
  });

  it('never converts the modelled hours into money', async () => {
    render(summary({ coworkValueEstimate: ESTIMATE, coworkFullRolloutEstimate: FULL_ESTIMATE }));

    // Hours are the whole output. A currency figure derived from a modelled number is how a model
    // ends up quoted as a saving, which is exactly what this estimate must not become.
    expect(screen.getByText('4,867\u20139,733 hours a month')).toBeTruthy();
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
    render(summary({ coworkValueEstimate: ESTIMATE }));

    const input = screen.getByRole('spinbutton', { name: 'Minutes saved per meeting' });
    await user.clear(input);
    await user.type(input, '10');

    // 4,800 x 10 + 64,000 x 1 + 9,600 x 3 = 140,800 minutes = 2,347 hours.
    expect(await screen.findByText('1,173\u20132,347 hours a month')).toBeTruthy();
    expect(screen.getByText(/^Using your figures\. Copilot: 10 min per meeting/)).toBeTruthy();
    expect(JSON.parse(sessionStorage.getItem(TIME_SAVED_STORAGE_KEY) ?? '{}')).toEqual({ meetingMinutes: 10 });

    await user.click(screen.getByRole('button', { name: 'Reset every figure to the product default' }));
    expect(await screen.findByText('973\u20131,947 hours a month')).toBeTruthy();
    expect(sessionStorage.getItem(TIME_SAVED_STORAGE_KEY)).toBeNull();
  });

  it('refuses a figure outside the bounds rather than modelling it', async () => {
    const user = userEvent.setup();
    render(summary({ coworkValueEstimate: ESTIMATE }));

    // Typed a character at a time, so the figure must be invalid at every step: "5", "50" on the way
    // to "500" are legitimate figures and would (correctly) be modelled as they are typed.
    const input = screen.getByRole('spinbutton', { name: 'Minutes saved per email' });
    await user.clear(input);
    await user.type(input, '-3');

    expect(await screen.findByRole('alert')).toHaveTextContent('Enter a number from 0 to 60.');
    expect(screen.getByText('973\u20131,947 hours a month')).toBeTruthy();
    expect(sessionStorage.getItem(TIME_SAVED_STORAGE_KEY)).toBeNull();
  });

  it('flags a model whose conservative end is above every published study', async () => {
    // 40 people over 20 days: 116,800 minutes x 50% / 800 person-days = 73 minutes a day at the
    // conservative end - well above the 14-27 the studies report.
    render(summary({ coworkValueEstimate: ESTIMATE }));

    expect(screen.getByText(/Even the conservative end is above the 14-27 minutes a day/)).toBeTruthy();
  });

  it('says so when the conservative end agrees with the published studies', async () => {
    // 200 x 5 + 1,000 x 1 + 400 x 3 = 3,200 minutes; x 50% / (4 people x 20 days) = 20 minutes a day.
    const modest = { ...ESTIMATE, cohortUsers: 4, addressableMeetings: 200, addressableMailThreads: 1000, addressableDocuments: 400 };
    render(summary({ coworkValueEstimate: modest }));

    expect(screen.getByText(/The conservative end sits within the 14-27 minutes a day/)).toBeTruthy();
  });

  it('cites a source for every piece of evidence, and says how each was obtained', async () => {
    render(summary({ coworkValueEstimate: ESTIMATE }));

    const links = screen
      .getAllByRole('link', { hidden: true })
      .map((a) => a.getAttribute('href') ?? '')
      .filter((href) => href.startsWith('https://'));
    expect(links.length).toBeGreaterThanOrEqual(6);
    expect(screen.getAllByText('Measured').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Self-reported').length).toBeGreaterThan(0);
    expect(screen.getByText(/no statistically significant change in how long documents took/)).toBeTruthy();
  });
});

describe('CoworkPanel Copilot and Cowork layers', () => {
  // The server always sends the Cowork options; most fixtures above predate them, so their Cowork
  // layer is zero and their figures are Copilot's alone.
  const coworkOptions = { ...OPTIONS, coworkMinutesSavedPerTask: 6, coworkAssumedTasksPerPersonPerMonth: 20 } as CopilotAdoptionOptions;

  // Copilot, as ESTIMATE: 116,800 minutes = 1,947 hours, 973 at the conservative end.
  // Cowork: 60 observed + 36 projected x 10 = 420 tasks x 6 minutes = 2,520 minutes = 42 hours, 21 conservative.
  const layered = {
    ...ESTIMATE,
    coworkTaskUsers: 4,
    observedCoworkTasks: 60,
    projectedCoworkUsers: 36,
    coworkTasksPerPersonPerMonth: 10,
    coworkTaskRateBasis: 'observed' as const,
    coworkTaskRateUsers: 4,
  };

  function renderLayered() {
    const s = summary({ options: coworkOptions, coworkValueEstimate: layered });
    return renderWithProvider(<CoworkPanel windowDays={28} summary={s} filterOptions={null} options={s.options} />);
  }

  beforeEach(() => {
    fetchCowork.mockReset();
    fetchCowork.mockResolvedValue(page([row({})]));
    resetTimeSavedStore();
  });

  /**
   * The defect this guards: the headline used to be one blended "Copilot and Cowork together"
   * figure, every minute of it resting on Microsoft 365 Copilot evidence - so the number quoted to
   * justify Copilot Credits was mostly Copilot's. Each layer is now shown on its own, with the
   * strength of the evidence behind it.
   */
  it('splits the headline into Copilot and Cowork, each badged with the strength of its evidence', async () => {
    renderLayered();

    expect(screen.getByText('994\u20131,989 hours a month')).toBeTruthy();
    const layers = screen.getByRole('group', { name: 'The headline split into Microsoft 365 Copilot and Cowork' });
    expect(within(layers).getByText('973\u20131,947 h a month')).toBeTruthy();
    expect(within(layers).getByText('21\u201342 h a month')).toBeTruthy();
    expect(within(layers).getByText('Backed by published studies')).toBeTruthy();
    expect(within(layers).getByText('Assumption - not yet studied')).toBeTruthy();
    expect(within(layers).getByText('420 Cowork tasks a month at 6 min each - the part paid for in Copilot Credits.')).toBeTruthy();
  });

  it('says no study has measured Cowork, and never credits Cowork with Copilot\u2019s evidence', async () => {
    renderLayered();

    expect(screen.getByText(/^No published study has measured Cowork\u2019s time savings/)).toBeTruthy();
    expect(screen.getByText(/Assumes each Cowork task saves a further 6 minutes on top of Copilot\. No study has yet measured/)).toBeTruthy();
    expect(
      screen.getByText(/Everyone else is projected at 10 tasks a month each: the average of the 4 people already running them/),
    ).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/Copilot and Cowork together/);
    // The Copilot cards describe what Copilot does; Cowork's operations live on Cowork's own card.
    expect(screen.queryByText(/^Before: Cowork/)).toBeNull();
    expect(screen.getByText(/^Communication: drafts and sends emails and follow-ups/)).toBeTruthy();
  });

  it('compares only the Copilot layer with published studies', async () => {
    renderLayered();

    // 116,800 Copilot minutes over 40 people x 20 working days: 146 a day, 73 at the conservative end.
    expect(screen.getByText(/Your Copilot assumptions give the average Copilot seat holder 73\u2013146 minutes a working day/)).toBeTruthy();
    expect(screen.getByText(/^Cowork\u2019s layer is left out of this comparison/)).toBeTruthy();
  });

  it('recomputes the Cowork layer from the reader\u2019s own task rate, and labels it as theirs', async () => {
    const user = userEvent.setup();
    renderLayered();

    const input = screen.getByRole('spinbutton', { name: 'Cowork tasks a month for each person not yet running them' });
    await user.clear(input);
    await user.type(input, '30');

    // 60 observed + 36 x 30 = 1,140 tasks x 6 = 6,840 minutes = 114 hours, 57 conservative.
    expect(await screen.findByText('1,030\u20132,061 hours a month')).toBeTruthy();
    expect(screen.getByText(/Everyone else is projected at 30 tasks a month each: your own figure\./)).toBeTruthy();
    expect(JSON.parse(sessionStorage.getItem(TIME_SAVED_STORAGE_KEY) ?? '{}')).toEqual({ tasksPerPerson: 30 });
  });

  it('says so plainly when the task rate is a placeholder because no Cowork use is observed', async () => {
    const s = summary({
      options: coworkOptions,
      coworkValueEstimate: {
        ...layered,
        coworkTaskUsers: 0,
        observedCoworkTasks: 0,
        projectedCoworkUsers: 40,
        coworkTasksPerPersonPerMonth: 20,
        coworkTaskRateBasis: 'assumed' as const,
        coworkTaskRateUsers: 0,
      },
    });
    renderWithProvider(<CoworkPanel windowDays={28} summary={s} filterOptions={null} options={s.options} />);

    expect(screen.getByText(/so everyone is projected at 20 tasks a month each: a placeholder until real use is observed\./)).toBeTruthy();
    expect(screen.getByText('placeholder - no Cowork tasks seen yet')).toBeTruthy();
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
    render(summary({ coworkValueEstimate: ESTIMATE }));
    expect(screen.getByRole('tab', { name: /Time saved/, selected: true })).toBeTruthy();

    await user.click(screen.getByRole('button', { name: 'Adjust the assumptions' }));

    expect(scrollIntoView).toHaveBeenCalled();
    expect(screen.getByRole('spinbutton', { name: 'Minutes saved per meeting' })).toHaveFocus();
  });

  it('opens the Time saved section from any other section to do it', async () => {
    const user = userEvent.setup();
    render(summary({ coworkValueEstimate: ESTIMATE }));
    await openSection(user, /People to enable/);
    expect(screen.queryByRole('spinbutton', { name: 'Minutes saved per meeting' })).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Adjust the assumptions' }));

    expect(screen.getByRole('tab', { name: /Time saved/, selected: true })).toBeTruthy();
    expect(screen.getByRole('spinbutton', { name: 'Minutes saved per meeting' })).toHaveFocus();
  });

  it('opens the policy list, scrolled into view, from the headline', async () => {
    const user = userEvent.setup();
    render(summary({ coworkValueEstimate: ESTIMATE }));
    await waitFor(() => expect(fetchCowork).toHaveBeenCalled());
    fetchCowork.mockClear();

    await user.click(screen.getByRole('button', { name: 'See the 2 people to enable' }));

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
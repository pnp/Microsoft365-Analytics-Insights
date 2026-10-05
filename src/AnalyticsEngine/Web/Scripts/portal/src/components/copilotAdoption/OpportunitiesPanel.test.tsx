import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import { loadCatalog } from '../../i18n';
import { PRINT_ROW_LIMIT, requestPrint, resetPrintPreparation } from '../shared/printPreparation';
import type {
  CopilotAdoptionOptions,
  CopilotAdoptionSummary,
  LicenceOpportunityPage,
  LicenceValueEstimate,
  OpportunityFilters,
} from '../../types/copilotAdoption';

const fetchOpportunities = vi.fn();

vi.mock('../../api/copilotAdoptionApi', () => ({
  fetchOpportunities: (...args: unknown[]) => fetchOpportunities(...args),
  opportunitiesExportUrl: () => '/api/CopilotAdoption/opportunities/export?windowDays=28',
}));

// Imported after the mock so the panel picks up the stubbed module.
const { default: OpportunitiesPanel } = await import('./OpportunitiesPanel');
const { resetTimeSavedStore, TIME_SAVED_STORAGE_KEY } = await import('./coworkTimeSaved');
const { resetTimeSavedCohortStore, TIME_SAVED_COHORT_STORAGE_KEY } = await import('./timeSavedCohort');

// The product defaults for the Copilot minutes: 5 a meeting, half a minute an email and 1 a document,
// with the conservative end at 50%. A 28-day month of 5-day weeks is 20 working days.
const OPTIONS = {
  windowDays: 28,
  workingDaysPerWeek: 5,
  habitBucketNormalisationDays: 28,
  opportunityUnlicensedCopilotWeight: 40,
  opportunityCollaborationWeight: 25,
  opportunityEmailWeight: 20,
  opportunityDocumentWeight: 15,
  opportunityCopilotTarget: 20,
  opportunityCopilotTargetBasisDays: 28,
  opportunityCollaborationTarget: 30,
  opportunityEmailTarget: 40,
  opportunityDocumentTarget: 20,
  opportunityRecommendScore: 60,
  opportunityProvenDemandMinActiveDays: 3,
  copilotMinutesSavedPerMeeting: 5,
  copilotMinutesSavedPerMailThread: 0.5,
  copilotMinutesSavedPerDocument: 1,
  coworkEstimateLowerBoundRatio: 0.5,
  coworkSendEmailShare: 0.05,
  coworkSendEmailMinutes: 6,
  maxOpportunityCandidates: 50000,
} as CopilotAdoptionOptions;

// The licence golden figure, shared with CopilotAdoptionLicenceEstimateTests and coworkTimeSaved.test:
// 1,234 meetings x 5 + 5,678 emails x 0.5 + 910 documents x 1 = 9,919 minutes = 165 hours, 83 at the
// conservative end - 103 from meetings, 47 from email and 15 from documents.
const RECOMMENDED: LicenceValueEstimate = {
  isModelled: true,
  cohortUsers: 10,
  addressableMeetings: 1234,
  addressableMailThreads: 5678,
  addressableDocuments: 910,
  hoursPerMonthLow: 83,
  hoursPerMonthHigh: 165,
  candidatesCapped: false,
  assumptions: [],
};

// The 4 of them already using Copilot Chat: 600 x 5 + 2,400 x 0.5 + 360 x 1 = 4,560 minutes = 76 hours.
const CHAT_USERS: LicenceValueEstimate = {
  ...RECOMMENDED,
  cohortUsers: 4,
  addressableMeetings: 600,
  addressableMailThreads: 2400,
  addressableDocuments: 360,
  hoursPerMonthLow: 38,
  hoursPerMonthHigh: 76,
};

// Every licence candidate, recommended or not - the 10 above and 30 lighter users: 2,000 x 5 + 8,000
// x 0.5 + 2,000 x 1 = 16,000 minutes = 267 hours, 133 at the conservative end. Over 40 people x 20
// working days that is 20 minutes a day each, 10 at the conservative end.
const ALL_CANDIDATES: LicenceValueEstimate = {
  ...RECOMMENDED,
  cohortUsers: 40,
  addressableMeetings: 2000,
  addressableMailThreads: 8000,
  addressableDocuments: 2000,
  hoursPerMonthLow: 133,
  hoursPerMonthHigh: 267,
};

function summary(overrides: Partial<CopilotAdoptionSummary> = {}): CopilotAdoptionSummary {
  return {
    options: OPTIONS,
    recommendedForLicence: 10,
    licenceOpportunityEstimate: RECOMMENDED,
    licenceChatUsersEstimate: CHAT_USERS,
    ...overrides,
  } as CopilotAdoptionSummary;
}

const EMPTY_PAGE: LicenceOpportunityPage = { total: 0, skip: 0, take: 50, rows: [], warnings: [] };

/**
 * Renders the panel and lets the candidate list's first load land. Without the wait that load
 * resolves after the test has finished, and React reports its state update as outside act().
 */
async function renderPanel(s: CopilotAdoptionSummary, language?: 'en' | 'es') {
  const result = renderWithProvider(
    <OpportunitiesPanel windowDays={28} summary={s} options={s.options} />,
    language ? { language } : undefined,
  );
  await waitFor(() => expect(fetchOpportunities).toHaveBeenCalled());
  return result;
}

const headline = (name = '83\u2013165 hours a month') => screen.getByRole('region', { name });

/** The tab is sectioned; the model's assertions start by opening the section they are about. */
async function openTimeSaved(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('tab', { name: 'Time saved' }));
  return screen.getByRole('tabpanel', { name: 'Time saved' });
}

const scrollIntoView = vi.fn();
const originalScrollIntoView = Element.prototype.scrollIntoView;

beforeEach(() => {
  fetchOpportunities.mockReset();
  fetchOpportunities.mockResolvedValue(EMPTY_PAGE);
  resetTimeSavedStore();
  resetTimeSavedCohortStore();
  scrollIntoView.mockReset();
  // jsdom implements no scrolling, so the call is recorded rather than performed.
  Element.prototype.scrollIntoView = scrollIntoView;
});

afterEach(() => {
  Element.prototype.scrollIntoView = originalScrollIntoView;
});

describe('OpportunitiesPanel licence headline', () => {
  it('leads with the time a licence could give back to the people it recommends', async () => {
    await renderPanel(summary());

    const hero = headline();
    expect(within(hero).getByText('Potential time back from licensing')).toBeTruthy();
    expect(within(hero).getByText('if the 10 people recommended for a licence were licensed')).toBeTruthy();
    expect(within(hero).getByText(/^Meetings, email and documents with Microsoft 365 Copilot/)).toBeTruthy();
    expect(
      within(hero).getByText('Using the product defaults: 5 min per meeting, 0.5 per email and 1 per document. Conservative end at 50%.'),
    ).toBeTruthy();
  });

  it('badges it as modelled, and as resting on published studies', async () => {
    await renderPanel(summary());

    const hero = headline();
    expect(within(hero).getByText('Modelled, not measured')).toBeTruthy();
    expect(within(hero).getByText('Backed by published studies')).toBeTruthy();
    // That is the Cowork estimate's badge, and only Cowork's: no study has measured Cowork.
    expect(screen.queryByText('Assumption - not yet studied')).toBeNull();
  });

  it('shows the recommended candidates already using Copilot Chat beside it', async () => {
    await renderPanel(summary());

    const hero = headline();
    expect(within(hero).getByText('38\u201376 h')).toBeTruthy();
    expect(within(hero).getByText('a month from the 4 people already using Copilot Chat')).toBeTruthy();
    expect(within(hero).getByText(/^The strongest part of the case: their demand is observed, not inferred\./)).toBeTruthy();
  });

  it('says so, rather than showing a zero, when none of them uses Copilot Chat', async () => {
    await renderPanel(summary({ licenceChatUsersEstimate: { ...CHAT_USERS, cohortUsers: 0 } }));

    const hero = headline();
    expect(within(hero).getByText('\u2014')).toBeTruthy();
    expect(
      within(hero).getByText('None of the recommended candidates used Copilot Chat in this period, so every recommendation rests on workload.'),
    ).toBeTruthy();
    expect(within(hero).queryByText(/^0\u2013/)).toBeNull();
  });

  it('restates it per person, and splits it by kind of work so the parts add up to the total', async () => {
    await renderPanel(summary());

    const hero = headline();
    // 9,919 minutes over 10 people x 20 working days: 50 minutes a day each, 25 at the conservative end.
    expect(within(hero).getByText('25\u201350 minutes')).toBeTruthy();
    expect(within(hero).getByText('a working day, for each person recommended')).toBeTruthy();
    // 103 + 47 + 15 = 165, and 62% + 29% + 9% = 100%.
    expect(within(hero).getByText('Meetings - 103 h (62%)')).toBeTruthy();
    expect(within(hero).getByText('Email - 47 h (29%)')).toBeTruthy();
    expect(within(hero).getByText('Documents - 15 h (9%)')).toBeTruthy();
    expect(
      within(hero).getByRole('img', { name: 'Modelled time by kind of work: meetings 62%, email 29%, documents 9%' }),
    ).toBeTruthy();
  });

  it('warns that the figure is a floor when the candidate list reached its cap', async () => {
    const user = userEvent.setup();
    await renderPanel(summary({ licenceOpportunityEstimate: { ...RECOMMENDED, candidatesCapped: true } }));

    expect(
      within(headline()).getByText(
        'The candidate list reached its 50,000-candidate limit, so people beyond it are not counted: treat these figures as a floor.',
      ),
    ).toBeTruthy();
    const model = await openTimeSaved(user);
    expect(
      within(model).getByText(
        'The candidate list reached its 50,000-candidate limit, so people beyond it are not counted and the true figure may be higher.',
      ),
    ).toBeTruthy();
  });

  it('says nothing about a cap the list did not reach', async () => {
    await renderPanel(summary());

    expect(screen.queryByText(/candidate limit/)).toBeNull();
  });

  it('never converts the modelled hours into money', async () => {
    await renderPanel(summary());

    expect(headline()).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/[£$€]\s?\d/);
    expect(screen.getAllByText(/This report reports seats, people and hours - never money\./).length).toBeGreaterThan(0);
  });

  it('shows the candidate list alone when there is no estimate to show', async () => {
    // Nobody recommended, or no Microsoft 365 usage reports to model from: the server sends an empty
    // estimate, and "0 hours" would read as a finding about the tenant.
    await renderPanel(summary({ licenceOpportunityEstimate: { ...RECOMMENDED, cohortUsers: 0 }, licenceChatUsersEstimate: undefined }));

    await waitFor(() => expect(fetchOpportunities).toHaveBeenCalled());
    expect(screen.queryByText(/hours a month$/)).toBeNull();
    expect(screen.queryByText('Modelled, not measured')).toBeNull();
    expect(screen.queryByRole('tab', { name: 'Time saved' })).toBeNull();
    expect(screen.getByRole('checkbox', { name: 'Recommended only' })).toBeTruthy();
  });

  it('still says the candidate list was capped when this view has nobody on it', async () => {
    // A filtered view whose people all ranked below the tenant-wide cut-off gets no headline, and the
    // headline was the only place the cap was said - so the list read "nobody in this tenant qualifies".
    await renderPanel(summary({
      licenceOpportunityEstimate: { ...RECOMMENDED, cohortUsers: 0, candidatesCapped: true },
      licenceChatUsersEstimate: undefined,
    }));

    expect(await screen.findByText(
      'The candidate list reached its 50,000-candidate limit for the whole tenant, so candidates ranked below it were never listed. People in this view who would qualify may be among them.',
    )).toBeTruthy();
    expect(screen.getByText('No candidate who made the ranked list is in this view for the selected period.')).toBeTruthy();
    expect(screen.queryByText(/Nobody in this tenant qualifies/)).toBeNull();
  });

  it('says nobody qualifies only when the list was complete', async () => {
    await renderPanel(summary({ licenceOpportunityEstimate: { ...RECOMMENDED, cohortUsers: 0 }, licenceChatUsersEstimate: undefined }));

    expect(await screen.findByText('Nobody in this tenant qualifies as a licence candidate for the selected period.')).toBeTruthy();
    expect(screen.queryByText(/candidate limit/)).toBeNull();
  });

  it('says nobody in the view qualifies, not nobody in the tenant, under a page-wide filter', async () => {
    // Only the people the page filter selects were searched; the rest of the tenant may hold candidates.
    const s = summary({ licenceOpportunityEstimate: { ...RECOMMENDED, cohortUsers: 0 }, licenceChatUsersEstimate: undefined });
    renderWithProvider(<OpportunitiesPanel windowDays={28} summary={s} options={s.options} userFilter="d:department~Sales" />);

    expect(await screen.findByText('Nobody in this view qualifies as a licence candidate for the selected period.')).toBeTruthy();
    expect(screen.queryByText(/Nobody in this tenant qualifies/)).toBeNull();
  });

  it('reads in Spanish', async () => {
    await loadCatalog('es');
    await renderPanel(summary(), 'es');

    const hero = await screen.findByRole('region', { name: '83\u2013165 horas al mes' });
    expect(within(hero).getByText('si las 10 personas recomendadas para una licencia la tuvieran')).toBeTruthy();
    expect(within(hero).getByText('Respaldado por estudios publicados')).toBeTruthy();
    expect(within(hero).getByText('al mes de las 4 personas que ya usan Copilot Chat')).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Candidatos', selected: true })).toBeTruthy();
  });
});

/**
 * The option to model every licence candidate as well as the people recommended. Recommended stays
 * the default - it is the purchase the list makes the case for - but on a tenant where nobody uses
 * Microsoft 365 heavily enough to be recommended it is empty, and the headline used to vanish.
 */
describe('OpportunitiesPanel every licence candidate', () => {
  const withAll = (overrides: Partial<CopilotAdoptionSummary> = {}) =>
    summary({ licenceAllCandidatesEstimate: ALL_CANDIDATES, ...overrides });

  const nobodyRecommended = () =>
    withAll({
      recommendedForLicence: 0,
      licenceOpportunityEstimate: { ...RECOMMENDED, cohortUsers: 0, hoursPerMonthLow: 0, hoursPerMonthHigh: 0 },
      licenceChatUsersEstimate: { ...CHAT_USERS, cohortUsers: 0, hoursPerMonthLow: 0, hoursPerMonthHigh: 0 },
    });

  it('leads with the people recommended, and offers every candidate beside them', async () => {
    await renderPanel(withAll());

    const hero = headline();
    expect(within(hero).getByRole('radio', { name: 'Recommended for a licence (10)' })).toBeChecked();
    expect(within(hero).getByRole('radio', { name: 'All candidates (40)' })).not.toBeChecked();
    expect(within(hero).getByText('if the 10 people recommended for a licence were licensed')).toBeTruthy();
    expect(within(hero).queryByText(/^Modelling every licence candidate/)).toBeNull();
    expect(sessionStorage.getItem(TIME_SAVED_COHORT_STORAGE_KEY)).toBeNull();
  });

  it('models every candidate when chosen, with the recommended people beside them', async () => {
    const user = userEvent.setup();
    await renderPanel(withAll());

    await user.click(within(headline()).getByRole('radio', { name: 'All candidates (40)' }));

    const hero = headline('133\u2013267 hours a month');
    expect(within(hero).getByText('if all 40 licence candidates were licensed')).toBeTruthy();
    expect(within(hero).getByText(/^Modelling every licence candidate, not only the people recommended/)).toBeTruthy();
    // The part of it the list stands behind, in place of the Chat users.
    expect(within(hero).getByText('83\u2013165 h')).toBeTruthy();
    expect(within(hero).getByText('a month from the 10 people recommended for a licence')).toBeTruthy();
    expect(within(hero).queryByText('a month from the 4 people already using Copilot Chat')).toBeNull();
    // 16,000 minutes over 40 people x 20 working days.
    expect(within(hero).getByText('10\u201320 minutes')).toBeTruthy();
    expect(within(hero).getByText('a working day, for each licence candidate')).toBeTruthy();
    expect(JSON.parse(sessionStorage.getItem(TIME_SAVED_COHORT_STORAGE_KEY) ?? '{}')).toEqual({ licence: 'all' });

    await user.click(within(hero).getByRole('radio', { name: 'Recommended for a licence (10)' }));
    expect(headline()).toBeTruthy();
    expect(sessionStorage.getItem(TIME_SAVED_COHORT_STORAGE_KEY)).toBeNull();
  });

  it('takes the working with it, and states the assumptions for every candidate', async () => {
    const user = userEvent.setup();
    await renderPanel(withAll());
    await user.click(within(headline()).getByRole('radio', { name: 'All candidates (40)' }));

    const model = await openTimeSaved(user);
    expect(within(model).getByRole('radio', { name: 'All 40 licence candidates' })).toBeChecked();
    expect(within(model).getByText('8,000')).toBeTruthy();
    expect(
      within(model).getByText(
        "Volumes are observed from Microsoft's usage reports for 40 licence candidates - everyone without a licence who used Microsoft 365 or Copilot Chat in the period, recommended or not - restated over 20 working days a month.",
      ),
    ).toBeTruthy();
    expect(within(model).getByText(/for every licence candidate, recommended or not;/)).toBeTruthy();
    // The sense check describes the people the headline models.
    expect(
      within(model).getByText(/^Your assumptions give the average licence candidate 10\u201320 minutes a working day\./),
    ).toBeTruthy();
    expect(within(model).getByText('This model: the average licence candidate')).toBeTruthy();

    // The Chat users can still be opened here, and the headline stays on the reader's choice.
    await user.click(within(model).getByRole('radio', { name: 'The 4 people already using Copilot Chat' }));
    expect(within(model).getByText('2,400')).toBeTruthy();
    expect(headline('133\u2013267 hours a month')).toBeTruthy();
  });

  it('shows exactly the people it counts: every candidate, with the list\u2019s filters lifted', async () => {
    const user = userEvent.setup();
    await renderPanel(withAll());
    await user.click(screen.getByRole('checkbox', { name: 'Recommended only' }));
    await user.click(within(headline()).getByRole('radio', { name: 'All candidates (40)' }));
    await openTimeSaved(user);
    fetchOpportunities.mockClear();

    await user.click(screen.getByRole('button', { name: 'See all 40 candidates' }));

    expect(screen.getByRole('tab', { name: 'Candidates', selected: true })).toBeTruthy();
    expect(screen.getByRole('checkbox', { name: 'Recommended only' })).not.toBeChecked();
    expect(scrollIntoView).toHaveBeenCalled();
    await waitFor(() => expect(fetchOpportunities).toHaveBeenCalled());
    const filters = fetchOpportunities.mock.calls.at(-1)![1] as OpportunityFilters;
    expect(filters.recommendedOnly).toBe(false);
    expect(filters.existingCopilotUsersOnly).toBe(false);
  });

  /** The customer this exists for. */
  it('models every candidate when nobody is recommended, and says why', async () => {
    const user = userEvent.setup();
    await renderPanel(nobodyRecommended());

    const hero = headline('133\u2013267 hours a month');
    expect(within(hero).getByText(/^Nobody is recommended for a licence in this period, so this models every licence candidate instead/)).toBeTruthy();
    expect(within(hero).getByRole('radio', { name: 'Recommended for a licence (0)' })).toBeDisabled();
    expect(within(hero).getByRole('radio', { name: 'All candidates (40)' })).toBeChecked();
    expect(within(hero).getByText('if all 40 licence candidates were licensed')).toBeTruthy();
    // No Chat-users figure for a recommended cohort that is empty, and no zero anywhere.
    expect(within(hero).queryByText(/already using Copilot Chat/)).toBeNull();
    expect(within(hero).queryByText(/^0\u2013/)).toBeNull();
    // The reader did not choose this, so it is not remembered as their choice.
    expect(sessionStorage.getItem(TIME_SAVED_COHORT_STORAGE_KEY)).toBeNull();

    const model = await openTimeSaved(user);
    expect(
      within(model).getByText(
        "Volumes are observed from Microsoft's usage reports for 40 licence candidates - everyone without a licence who used Microsoft 365 or Copilot Chat in the period, recommended or not - restated over 20 working days a month.",
      ),
    ).toBeTruthy();
    // One cohort to show, so nothing to choose between.
    expect(within(model).queryByRole('radiogroup')).toBeNull();
  });

  it('keeps the candidate list alone when there are no candidates to model either', async () => {
    await renderPanel(
      summary({
        licenceOpportunityEstimate: { ...RECOMMENDED, cohortUsers: 0 },
        licenceChatUsersEstimate: undefined,
        licenceAllCandidatesEstimate: { ...ALL_CANDIDATES, cohortUsers: 0 },
      }),
    );

    expect(screen.queryByText(/hours a month$/)).toBeNull();
    expect(screen.queryByRole('tab', { name: 'Time saved' })).toBeNull();
  });

  it('reads in Spanish', async () => {
    const user = userEvent.setup();
    await loadCatalog('es');
    await renderPanel(withAll(), 'es');

    await user.click(await screen.findByRole('radio', { name: 'Todos los candidatos (40)' }));

    const hero = await screen.findByRole('region', { name: '133\u2013267 horas al mes' });
    expect(within(hero).getByText('si los 40 candidatos a una licencia la tuvieran')).toBeTruthy();
    expect(within(hero).getByText('al mes de las 10 personas recomendadas para una licencia')).toBeTruthy();
    expect(within(hero).getByText(/^Se modela a todos los candidatos a una licencia/)).toBeTruthy();
  });
});

describe('OpportunitiesPanel sections and actions', () => {
  it('opens on the candidate list, the tab\u2019s purpose', async () => {
    await renderPanel(summary());

    expect(screen.getByRole('tab', { name: 'Candidates', selected: true })).toBeTruthy();
    expect(screen.getByRole('tabpanel', { name: 'Candidates' })).toBeTruthy();
    // Hidden, so it has no accessible role until it is opened.
    expect(screen.queryByRole('tabpanel', { name: 'Time saved' })).toBeNull();
    await waitFor(() => expect(fetchOpportunities).toHaveBeenCalled());
  });

  it('shows exactly the people the headline counts, scrolled into view', async () => {
    const user = userEvent.setup();
    await renderPanel(summary());
    await openTimeSaved(user);
    await waitFor(() => expect(fetchOpportunities).toHaveBeenCalled());
    fetchOpportunities.mockClear();

    await user.click(screen.getByRole('button', { name: 'See the 10 people recommended' }));

    expect(screen.getByRole('tab', { name: 'Candidates', selected: true })).toBeTruthy();
    expect(screen.getByRole('checkbox', { name: 'Recommended only' })).toBeChecked();
    expect(scrollIntoView).toHaveBeenCalled();
    await waitFor(() => expect(fetchOpportunities).toHaveBeenCalled());
    expect((fetchOpportunities.mock.calls.at(-1)![1] as OpportunityFilters).recommendedOnly).toBe(true);
  });

  it('takes the reader to the editable figures from the candidate list', async () => {
    const user = userEvent.setup();
    await renderPanel(summary());

    await user.click(screen.getByRole('button', { name: 'Adjust the assumptions' }));

    expect(screen.getByRole('tab', { name: 'Time saved', selected: true })).toBeTruthy();
    expect(scrollIntoView).toHaveBeenCalled();
    expect(screen.getByRole('spinbutton', { name: 'Minutes saved per meeting' })).toHaveFocus();
  });

  /**
   * The same defect the Cowork tab guards: with the section already open, a plain section switch
   * changes nothing on screen and the button reads as broken.
   */
  it('still does something when the Time saved section is already open', async () => {
    const user = userEvent.setup();
    await renderPanel(summary());
    await openTimeSaved(user);
    scrollIntoView.mockClear();

    await user.click(screen.getByRole('button', { name: 'Adjust the assumptions' }));

    expect(scrollIntoView).toHaveBeenCalled();
    expect(screen.getByRole('spinbutton', { name: 'Minutes saved per meeting' })).toHaveFocus();
  });
});

describe('OpportunitiesPanel without See PII', () => {
  it('opens on the licence estimate, never asks for the candidates, and offers no list to open', async () => {
    renderWithProvider(
      <OpportunitiesPanel windowDays={28} summary={summary()} filterOptions={null} options={OPTIONS} canSeePii={false} />,
    );

    expect(screen.getByRole('tab', { name: 'Time saved', selected: true })).toBeTruthy();
    expect(headline()).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'See the 10 people recommended' })).toBeNull();

    const user = userEvent.setup();
    await user.click(screen.getByRole('tab', { name: 'Candidates' }));
    expect(within(screen.getByRole('tabpanel', { name: 'Candidates' })).getByText('Individual details are hidden')).toBeTruthy();
    expect(fetchOpportunities).not.toHaveBeenCalled();
  });

  it('shows only the note when there is no estimate to lead with', () => {
    renderWithProvider(
      <OpportunitiesPanel
        windowDays={28}
        summary={summary({ licenceOpportunityEstimate: { ...RECOMMENDED, cohortUsers: 0 }, licenceChatUsersEstimate: undefined })}
        filterOptions={null}
        options={OPTIONS}
        canSeePii={false}
      />,
    );

    expect(screen.getByText('Individual details are hidden')).toBeTruthy();
    expect(fetchOpportunities).not.toHaveBeenCalled();
  });
});

describe('OpportunitiesPanel licence model', () => {
  it('recomputes the headline from the reader\u2019s own figure, and the sense check follows it', async () => {
    const user = userEvent.setup();
    await renderPanel(summary());
    const model = await openTimeSaved(user);
    expect(within(model).getByText(/^The conservative end sits within the 14-27 minutes a day/)).toBeTruthy();

    const input = within(model).getByRole('spinbutton', { name: 'Minutes saved per meeting' });
    await user.clear(input);
    await user.type(input, '20');

    // 1,234 x 20 + 2,839 + 910 = 28,429 minutes = 474 hours, 237 conservative - 71 minutes a day each
    // at the conservative end, above every published study.
    expect(await screen.findByText('237\u2013474 hours a month')).toBeTruthy();
    expect(
      screen.getByText('Using your figures: 20 min per meeting, 0.5 per email and 1 per document. Conservative end at 50%.'),
    ).toBeTruthy();
    expect(within(model).getByText(/^Even the conservative end is above the 14-27 minutes a day/)).toBeTruthy();
    expect(JSON.parse(sessionStorage.getItem(TIME_SAVED_STORAGE_KEY) ?? '{}')).toEqual({ meetingMinutes: 20 });
  });

  it('refuses a figure outside the bounds rather than modelling it', async () => {
    const user = userEvent.setup();
    await renderPanel(summary());
    const model = await openTimeSaved(user);

    const input = within(model).getByRole('spinbutton', { name: 'Minutes saved per email' });
    await user.clear(input);
    await user.type(input, '-3');

    expect(await within(model).findByRole('alert')).toHaveTextContent('Enter a number from 0 to 60.');
    expect(headline()).toBeTruthy();
    expect(sessionStorage.getItem(TIME_SAVED_STORAGE_KEY)).toBeNull();
  });

  it('sense-checks the average recommended candidate against published studies', async () => {
    const user = userEvent.setup();
    await renderPanel(summary());
    const model = await openTimeSaved(user);

    expect(
      within(model).getByText(/^Your assumptions give the average recommended candidate 25\u201350 minutes a working day\./),
    ).toBeTruthy();
    expect(within(model).getByText('This model: the average recommended candidate')).toBeTruthy();
    expect(within(model).getByText('25\u201350 min a day')).toBeTruthy();
  });

  it('sets out the evidence behind each figure, and says how each was obtained', async () => {
    const user = userEvent.setup();
    await renderPanel(summary());
    const model = await openTimeSaved(user);

    expect(within(model).getByText('Meetings: 5 min each')).toBeTruthy();
    expect(within(model).getByText('Email: 0.5 min each')).toBeTruthy();
    expect(within(model).getByText('Documents: 1 min each')).toBeTruthy();
    expect(within(model).getByText('The conservative end: 50% of every assumption')).toBeTruthy();
    const links = within(model)
      .getAllByRole('link')
      .map((a) => a.getAttribute('href') ?? '')
      .filter((href) => href.startsWith('https://'));
    expect(links.length).toBeGreaterThanOrEqual(6);
    expect(within(model).getAllByText('Measured').length).toBeGreaterThan(0);
    expect(within(model).getAllByText('Self-reported').length).toBeGreaterThan(0);
    expect(within(model).getByText(/no statistically significant change in how long documents took/)).toBeTruthy();
  });

  it('states the estimate\u2019s assumptions from the facts in force', async () => {
    const user = userEvent.setup();
    await renderPanel(summary());
    const model = await openTimeSaved(user);

    expect(
      within(model).getByText('Assumes Microsoft 365 Copilot saves 5 minutes per meeting, 0.5 per email and 1 per document.'),
    ).toBeTruthy();
    expect(
      within(model).getByText(
        "Volumes are observed from Microsoft's usage reports for 10 recommended licence candidates, restated over 20 working days a month.",
      ),
    ).toBeTruthy();
    expect(within(model).getByText(/^Candidates already using Copilot Chat without a licence may be realising part of this/)).toBeTruthy();
    expect(
      within(model).getByText('The lower bound applies 50% of each minutes-saved assumption; the upper bound applies them in full.'),
    ).toBeTruthy();
    expect(within(model).getByText(/^This is the potential at full use of a licence, not a forecast/)).toBeTruthy();
    expect(within(model).getByText(/These figures are a model for sizing a licence purchase, not a result\./)).toBeTruthy();
    expect(within(model).getByText(/This report reports seats, people and hours - never money\./)).toBeTruthy();
  });

  it('shows the working for the candidates already using Copilot Chat too', async () => {
    const user = userEvent.setup();
    await renderPanel(summary());
    const model = await openTimeSaved(user);

    expect(within(model).getByRole('radio', { name: 'All 10 people recommended' })).toBeChecked();
    await user.click(within(model).getByRole('radio', { name: 'The 4 people already using Copilot Chat' }));

    expect(
      within(model).getByText(
        "Volumes are observed from Microsoft's usage reports for 4 recommended licence candidates, restated over 20 working days a month.",
      ),
    ).toBeTruthy();
    expect(within(model).getByText('2,400')).toBeTruthy();
    // The calculator shows its working for another cohort; the headline stays on the decision.
    expect(headline()).toBeTruthy();
  });

  it('has nothing of Cowork in it', async () => {
    const user = userEvent.setup();
    await renderPanel(summary());
    const model = await openTimeSaved(user);

    expect(within(model).queryByRole('spinbutton', { name: 'Minutes saved per Cowork task' })).toBeNull();
    expect(within(model).queryByText(/^Assumes each Cowork task saves/)).toBeNull();
    // The one mention: the two figures it shares with the Cowork estimate.
    expect(
      within(model).getByText(/^The conservative share applies to every time-saved estimate - licensing, Cowork and the time already saved by seat holders/),
    ).toBeTruthy();
  });

  it('prints each assumption as the figure it is set to, since the box is not printed', async () => {
    const user = userEvent.setup();
    await renderPanel(summary());
    const model = await openTimeSaved(user);

    const box = within(model).getByRole('spinbutton', { name: 'Minutes saved per meeting' });
    expect(box.closest('[data-print="hide"]')).not.toBeNull();
    const printed = [...model.querySelectorAll('[data-print="only"]')].map((n) => n.textContent);
    expect(printed).toContain('5 min');
  });

  it('prints which cohort the working is shown for, since the radio buttons are not printed', async () => {
    const user = userEvent.setup();
    await renderPanel(summary());
    const model = await openTimeSaved(user);

    expect(within(model).getByRole('radiogroup').closest('[data-print="hide"]')).not.toBeNull();
    const printed = [...model.querySelectorAll('[data-print="only"]')].map((n) => n.textContent);
    expect(printed).toContain('Working shown for: All 10 people recommended');
  });
});

// Rendering a hundred-odd rows through Fluent in jsdom takes seconds, and a CI runner is slower.
describe('OpportunitiesPanel printing', { timeout: 30000 }, () => {
  const upn = (id: number) => `demo.user${String(id).padStart(7, '0')}@contoso.example`;

  function candidate(id: number) {
    return {
      userId: id,
      userPrincipalName: upn(id),
      mail: null,
      department: 'Finance',
      jobTitle: null,
      country: null,
      officeLocation: null,
      companyName: null,
      manager: null,
      unlicensedCopilotInteractions: 0,
      unlicensedCopilotActiveDays: 0,
      lastCopilotInteractionUtc: null,
      teamsMessages: 10,
      teamsMeetings: 2,
      emailsSent: 5,
      emailsRead: 9,
      filesViewedOrEdited: 3,
      lastM365ActivityUtc: null,
      opportunityScore: 70,
      copilotDemandScore: 0,
      collaborationScore: 60,
      emailScore: 50,
      documentScore: 40,
      recommended: true,
      qualificationTier: null,
      qualificationTierLabel: null,
      rationale: 'Workload inferred.',
    };
  }

  /** A server holding `total` candidates, paging and clamping exactly as the API does. */
  function serve(total: number) {
    fetchOpportunities.mockImplementation(async (_windowDays: number, _filters: unknown, skip: number, take: number) => {
      const count = Math.max(0, Math.min(take, 500, total - skip));
      return {
        total,
        skip,
        take,
        warnings: [],
        rows: Array.from({ length: count }, (_, i) => candidate(skip + i + 1)),
      };
    });
  }

  const candidates = () => screen.getByRole('tabpanel', { name: 'Candidates' });
  const listed = () => within(candidates()).queryAllByText(/^demo\.user\d+@contoso\.example$/).length;

  beforeEach(() => resetPrintPreparation());
  afterEach(() => {
    vi.restoreAllMocks();
    resetPrintPreparation();
  });

  it('prints every candidate rather than the page on screen, open when every row was expanded', async () => {
    serve(60);
    const user = userEvent.setup();
    await renderPanel(summary());
    await waitFor(() => expect(listed()).toBe(50));
    await user.click(within(candidates()).getByRole('button', { name: 'Expand all' }));

    const printed = { rows: -1, details: -1 };
    vi.spyOn(window, 'print').mockImplementation(() => {
      printed.rows = listed();
      printed.details = within(candidates()).queryAllByText('Justification').length;
    });
    await act(async () => {
      await expect(requestPrint()).resolves.toEqual({ kind: 'printed' });
    });

    expect(printed).toEqual({ rows: 60, details: 60 });
    expect(listed()).toBe(50);
  });

  it('refuses, rather than printing one page, when the list is longer than can be printed', async () => {
    serve(PRINT_ROW_LIMIT * 3);
    await renderPanel(summary());
    await waitFor(() => expect(listed()).toBe(50));
    vi.spyOn(window, 'print').mockImplementation(() => {});

    await expect(requestPrint()).resolves.toMatchObject({ kind: 'tooManyRows' });
    expect(window.print).not.toHaveBeenCalled();
  });

  it('does not hold up a print of the time-saved section', async () => {
    serve(PRINT_ROW_LIMIT * 3);
    const user = userEvent.setup();
    await renderPanel(summary());
    await waitFor(() => expect(listed()).toBe(50));
    await openTimeSaved(user);
    vi.spyOn(window, 'print').mockImplementation(() => {});

    await expect(requestPrint()).resolves.toEqual({ kind: 'printed' });
  });

  it('keeps the filter bar and the pager off paper, and prints what the bar was set to', async () => {
    serve(130);
    const user = userEvent.setup();
    await renderPanel(summary());
    await waitFor(() => expect(listed()).toBe(50));
    const list = candidates();

    for (const control of [
      within(list).getByRole('checkbox', { name: 'Recommended only' }),
      within(list).getByRole('button', { name: 'Expand all' }),
      within(list).getByRole('button', { name: 'Next' }),
    ]) {
      expect(control.closest('[data-print="hide"]')).not.toBeNull();
    }
    // Who the list is about is the page-wide filter's job; the list has no department filter of its own.
    expect(within(list).queryByRole('combobox', { name: /department/i })).not.toBeInTheDocument();

    await user.click(within(list).getByRole('checkbox', { name: 'Recommended only' }));
    await waitFor(() =>
      expect(list.querySelector('[data-print="only"]')?.textContent).toBe('Filters: Recommended only'),
    );
  });
});

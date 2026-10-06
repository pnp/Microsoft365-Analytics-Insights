import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../test/renderWithProvider';
import ActivityAnalysisPage from './ActivityAnalysisPage';
import {
  fetchActivityAnalysisAvailability,
  fetchActivityAnalysisPeople,
  fetchActivityAnalysisReport,
} from '../api/activityAnalysisApi';
import { fetchUserFilterDimensions, fetchUserFilterValues } from '../api/userFilterApi';
import { resetUserFilterDimensionsCache } from '../components/userFilter/useUserFilterDimensions';
import type {
  ActivityAnalysisAvailability,
  ActivityAnalysisMetric,
  ActivityAnalysisPeople,
  ActivityAnalysisPeopleQuery,
  ActivityAnalysisQuery,
  ActivityAnalysisReport,
} from '../types/activityAnalysis';

vi.mock('../api/activityAnalysisApi', () => ({
  fetchActivityAnalysisAvailability: vi.fn(),
  fetchActivityAnalysisReport: vi.fn(),
  fetchActivityAnalysisPeople: vi.fn(),
}));

vi.mock('../api/userFilterApi', () => ({
  fetchUserFilterDimensions: vi.fn(),
  fetchUserFilterValues: vi.fn(),
}));

// The page debounces the metric selection and renders a good deal of Fluent UI per step.
vi.setConfig({ testTimeout: 30000 });

const TEAMS_CORE = [
  'teams.privateChats',
  'teams.teamChats',
  'teams.calls',
  'teams.meetings',
  'teams.meetingsAttended',
  'teams.meetingsOrganized',
];

function metric(key: string, category: string, overrides: Partial<ActivityAnalysisMetric> = {}): ActivityAnalysisMetric {
  return { key, category, unit: 'count', core: true, available: true, label: key, ...overrides };
}

const AVAILABILITY: ActivityAnalysisAvailability = {
  available: true,
  reason: null,
  earliestWeek: '2025-01-06',
  latestWeek: '2026-09-28',
  defaultFrom: '2025-10-06',
  defaultTo: '2026-09-28',
  maximumWeeks: 105,
  categories: ['teams', 'outlook', 'onedrive', 'sharepoint', 'copilot', 'vivaEngage'],
  metrics: [
    ...TEAMS_CORE.map((key) => metric(key, 'teams')),
    metric('teams.audioDuration', 'teams', { unit: 'seconds', core: false }),
    metric('outlook.emailsSent', 'outlook'),
    metric('outlook.emailsReceived', 'outlook'),
    metric('copilot.chats', 'copilot'),
    metric('vivaEngage.posted', 'vivaEngage', { available: false }),
  ],
};

const WEEKS = ['2026-09-07', '2026-09-14', '2026-09-21', '2026-09-28'];

/** A synthetic report for whatever the page asked for. Durations are 7,200 seconds - two hours. */
function report(query: ActivityAnalysisQuery, overrides: Partial<ActivityAnalysisReport> = {}): ActivityAnalysisReport {
  const values = (base: number) =>
    query.metrics.map((key, i) => ({ metric: key, sum: key === 'teams.audioDuration' ? 7200 : base * (i + 1), unique: base }));
  return {
    generatedUtc: '2026-10-06T10:00:00Z',
    from: query.from,
    to: query.to,
    weekStarts: WEEKS,
    metrics: query.metrics,
    populationPeople: 120,
    matchingPeople: 95,
    activePeople: 90,
    suppressed: false,
    series: query.metrics.map((key, i) => ({ metric: key, sum: [1, 2, 3, 4].map((v) => v * (i + 1)), activePeople: [1, 2, 3, 4] })),
    byCompany: {
      rows: [
        { name: 'Contoso', other: false, activePeople: 60 },
        { name: 'Fabrikam', other: false, activePeople: 30 },
      ],
      otherGroups: 0,
    },
    byDepartment: {
      rows: [
        { name: 'Sales', other: false, activePeople: 40 },
        { name: 'Marketing', other: false, activePeople: 30 },
        { name: null, other: false, activePeople: 20 },
      ],
      otherGroups: 0,
    },
    departments: [
      { name: 'Marketing', other: false, people: 30, values: values(11) },
      { name: 'Sales', other: false, people: 40, values: values(12) },
      { name: null, other: false, people: 25, values: values(5) },
    ],
    otherDepartments: 0,
    total: { people: 95, values: values(28) },
    licences: [
      { id: 7, name: 'Microsoft 365 E3', skuId: 'SPE_E3', people: 80 },
      { id: 9, name: 'Microsoft 365 E5', skuId: 'SPE_E5', people: 15 },
    ],
    rangeMaxima: [
      { metric: 'teams.calls', max: 412 },
      { metric: 'teams.audioDuration', max: 36000 },
    ],
    userFilter: null,
    ...overrides,
  };
}

function people(query: ActivityAnalysisPeopleQuery): ActivityAnalysisPeople {
  if (query.department === 'Sales') {
    return {
      department: 'Sales',
      noDepartment: false,
      totalPeople: 40,
      truncated: true,
      people: [
        { userPrincipalName: 'user1@contoso.com', department: 'Sales', values: query.metrics.map((m) => ({ metric: m, sum: 30 })) },
        { userPrincipalName: 'user2@contoso.com', department: 'Sales', values: query.metrics.map((m) => ({ metric: m, sum: 20 })) },
      ],
    };
  }
  if (query.noDepartment) {
    return {
      department: null,
      noDepartment: true,
      totalPeople: 1,
      truncated: false,
      people: [{ userPrincipalName: 'user9@fabrikam.com', department: null, values: query.metrics.map((m) => ({ metric: m, sum: 4 })) }],
    };
  }
  return {
    department: null,
    noDepartment: false,
    totalPeople: 95,
    truncated: true,
    people: [{ userPrincipalName: 'champion@contoso.com', department: 'Marketing', values: query.metrics.map((m) => ({ metric: m, sum: 99 })) }],
  };
}

const reportMock = vi.mocked(fetchActivityAnalysisReport);
const peopleMock = vi.mocked(fetchActivityAnalysisPeople);

function lastQuery(): ActivityAnalysisQuery {
  const calls = reportMock.mock.calls;
  return calls[calls.length - 1][0];
}

const NO_PII = { administration: true, seePii: false };

beforeEach(() => {
  vi.clearAllMocks();
  resetUserFilterDimensionsCache();
  vi.mocked(fetchActivityAnalysisAvailability).mockResolvedValue(AVAILABILITY);
  reportMock.mockImplementation(async (query) => report(query));
  peopleMock.mockImplementation(async (query) => people(query));
  vi.mocked(fetchUserFilterDimensions).mockResolvedValue({
    people: 120,
    loadedUtc: '2026-10-06T00:00:00Z',
    dimensions: [
      { key: 'department', kind: 'entra', name: null, orgTypeId: null, distinctValues: 3, peopleWithValue: 95, supportsTextMatch: true, fixedValues: false },
    ],
  });
  vi.mocked(fetchUserFilterValues).mockResolvedValue({
    dimension: 'department',
    values: [
      { value: 'Sales', people: 40 },
      { value: 'Marketing', people: 30 },
    ],
    totalMatching: 2,
    truncated: false,
    peopleWithoutValue: 25,
  });
});

describe('ActivityAnalysisPage availability', () => {
  it('explains that the profiling tables are missing, and asks for nothing else', async () => {
    vi.mocked(fetchActivityAnalysisAvailability).mockResolvedValue({
      ...AVAILABILITY,
      available: false,
      reason: 'notInstalled',
      earliestWeek: null,
      latestWeek: null,
      defaultFrom: null,
      defaultTo: null,
      metrics: [],
    });
    renderWithProvider(<ActivityAnalysisPage />);

    expect(await screen.findByText('Activity analysis is not set up on this deployment')).toBeVisible();
    expect(screen.getByText(/profiling runbooks in Azure Automation/)).toBeVisible();
    expect(screen.getByRole('link', { name: 'Open Profiling' })).toHaveAttribute('href', '#/admin/profiling');
    expect(screen.queryByRole('button', { name: /Filters/ })).not.toBeInTheDocument();
    expect(reportMock).not.toHaveBeenCalled();
  });

  it('says when the tables exist but no week has been compiled, without an admin link for a reader', async () => {
    vi.mocked(fetchActivityAnalysisAvailability).mockResolvedValue({ ...AVAILABILITY, available: false, reason: 'noData' });
    renderWithProvider(<ActivityAnalysisPage />, { access: { administration: false, seePii: false } });

    expect(await screen.findByText('No weekly activity has been compiled yet')).toBeVisible();
    expect(screen.getByText(/An administrator can check the runbooks/)).toBeVisible();
    expect(screen.queryByRole('link', { name: 'Open Profiling' })).not.toBeInTheDocument();
    expect(reportMock).not.toHaveBeenCalled();
  });

  it('offers a retry when availability cannot be read', async () => {
    const user = userEvent.setup();
    vi.mocked(fetchActivityAnalysisAvailability)
      .mockRejectedValueOnce(new Error('Couldn’t check whether activity analysis is available (500).'))
      .mockResolvedValueOnce(AVAILABILITY);
    renderWithProvider(<ActivityAnalysisPage />);

    expect(await screen.findByText('Couldn’t check whether activity analysis is available (500).')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('Results by department')).toBeVisible();
  });
});

describe('ActivityAnalysisPage figures', () => {
  it('opens on the core Teams metrics for the server’s default period', async () => {
    renderWithProvider(<ActivityAnalysisPage />);

    await waitFor(() => expect(reportMock).toHaveBeenCalledTimes(1));
    expect(lastQuery()).toEqual({
      from: '2025-10-06',
      to: '2026-09-28',
      metrics: TEAMS_CORE,
      userFilter: null,
      licences: [],
      ranges: [],
    });

    expect(await screen.findByText('95 matching people')).toBeVisible();
    expect(screen.getByText('90 active')).toBeVisible();
    expect(screen.getByText('Active people by company')).toBeVisible();
    expect(screen.getByText('Active people by department')).toBeVisible();
    expect(screen.getByText('Metrics by week')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Last 12 months' })).toHaveAttribute('aria-pressed', 'true');

    const matrix = screen.getByRole('region', { name: 'Results by department' });
    expect(within(matrix).getByRole('rowheader', { name: /Sales/ })).toBeInTheDocument();
    expect(within(matrix).getByRole('rowheader', { name: /\(no department\)/ })).toBeInTheDocument();
    expect(within(matrix).getByRole('rowheader', { name: /Total/ })).toBeInTheDocument();
    expect(within(matrix).getAllByRole('columnheader', { name: /Sum/ })).toHaveLength(TEAMS_CORE.length);
  });

  it('asks again with the new selection once it settles, keeping catalogue order', async () => {
    const user = userEvent.setup();
    renderWithProvider(<ActivityAnalysisPage />);
    await waitFor(() => expect(reportMock).toHaveBeenCalledTimes(1));
    await screen.findByText('95 matching people');

    await user.click(screen.getByRole('button', { name: 'Show the Outlook metrics' }));
    expect(screen.getByRole('button', { name: 'Hide the Outlook metrics' })).toHaveAttribute('aria-expanded', 'true');

    // Ticked last, but sent in the catalogue's order - after the Teams metrics.
    await user.click(screen.getByRole('checkbox', { name: 'Emails sent' }));
    await user.click(screen.getByRole('checkbox', { name: 'Teams calls' }));

    await waitFor(
      () =>
        expect(lastQuery().metrics).toEqual([
          'teams.privateChats',
          'teams.teamChats',
          'teams.meetings',
          'teams.meetingsAttended',
          'teams.meetingsOrganized',
          'outlook.emailsSent',
        ]),
      { timeout: 3000 },
    );
    // Debounced: two quick changes are at most two requests, normally one.
    expect(reportMock.mock.calls.length).toBeLessThanOrEqual(3);
    expect(screen.getByRole('checkbox', { name: 'Teams' })).toBePartiallyChecked();
    expect(screen.getByRole('checkbox', { name: 'Outlook' })).toBePartiallyChecked();
  });

  it('selects a whole category from its checkbox and shows durations in hours', async () => {
    const user = userEvent.setup();
    renderWithProvider(<ActivityAnalysisPage />);
    await screen.findByText('95 matching people');

    // Teams starts with its six core metrics ticked, so its box is mixed until audio time is added too.
    const teams = screen.getByRole('checkbox', { name: 'Teams' });
    expect(teams).toBePartiallyChecked();
    await user.click(teams);

    await waitFor(() => expect(lastQuery().metrics).toEqual([...TEAMS_CORE, 'teams.audioDuration']), { timeout: 3000 });
    const matrix = await screen.findByRole('region', { name: 'Results by department' });
    await waitFor(() => expect(within(matrix).getByText('Teams audio time (hours)')).toBeInTheDocument());
    // 7,200 seconds of audio, shown as 2 hours in every row.
    const total = within(matrix).getByRole('rowheader', { name: /Total/ }).closest('tr')!;
    expect(within(total).getByText('2')).toBeInTheDocument();
  });

  it('moves a custom period to the Mondays of its weeks before asking for it', async () => {
    const user = userEvent.setup();
    renderWithProvider(<ActivityAnalysisPage />);
    await screen.findByText('95 matching people');

    await user.click(screen.getByRole('button', { name: 'Custom' }));
    // A Wednesday and a Thursday: the server reads whole weeks, so the page asks for their Mondays.
    fireEvent.change(screen.getByLabelText('First week'), { target: { value: '2026-01-07' } });
    fireEvent.change(screen.getByLabelText('Last week'), { target: { value: '2026-04-02' } });
    await user.click(screen.getByRole('button', { name: 'Apply' }));

    await waitFor(() => expect(lastQuery()).toMatchObject({ from: '2026-01-05', to: '2026-03-30' }));
    expect(screen.getByText('5 Jan 2026 – 30 Mar 2026 · 13 weeks')).toBeVisible();
  });

  it('refuses a custom period that ends before it starts, and says why', async () => {
    const user = userEvent.setup();
    renderWithProvider(<ActivityAnalysisPage />);
    await screen.findByText('95 matching people');

    await user.click(screen.getByRole('button', { name: 'Custom' }));
    fireEvent.change(screen.getByLabelText('First week'), { target: { value: '2026-09-28' } });
    fireEvent.change(screen.getByLabelText('Last week'), { target: { value: '2026-01-05' } });
    await user.click(screen.getByRole('button', { name: 'Apply' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The first week must be on or before the last week.');
    expect(reportMock).toHaveBeenCalledTimes(1);
  });

  it('keeps the previous figures on screen, dimmed, while new ones load', async () => {
    const user = userEvent.setup();
    renderWithProvider(<ActivityAnalysisPage />);
    await screen.findByText('95 matching people');

    reportMock.mockImplementation(() => new Promise(() => {}));
    await user.click(screen.getByRole('button', { name: 'Last 3 months' }));

    await waitFor(() => expect(reportMock).toHaveBeenCalledTimes(2));
    expect(lastQuery()).toMatchObject({ from: '2026-07-06', to: '2026-09-28' });
    expect(screen.getByText('Updating the figures…')).toBeVisible();
    expect(screen.getByText('95 matching people')).toBeVisible();
    expect(screen.getByRole('region', { name: 'Results by department' })).toBeInTheDocument();
  });

  it('hides every figure when the server suppresses a group too small to show', async () => {
    reportMock.mockImplementation(async (query) =>
      report(query, {
        matchingPeople: 3,
        suppressed: true,
        series: [],
        byCompany: { rows: [], otherGroups: 0 },
        byDepartment: { rows: [], otherGroups: 0 },
        departments: [],
        total: { people: 3, values: [] },
      }),
    );
    renderWithProvider(<ActivityAnalysisPage />, { access: NO_PII });

    expect(await screen.findByText('Figures hidden')).toBeVisible();
    expect(screen.getByText(/Fewer than 5 people match these filters/)).toBeVisible();
    expect(screen.queryByText('Active people by company')).not.toBeInTheDocument();
    expect(screen.queryByText('Metrics by week')).not.toBeInTheDocument();
    expect(screen.queryByText('Results by department')).not.toBeInTheDocument();
    expect(screen.queryByText('Individual details are hidden')).not.toBeInTheDocument();
  });

  it('shows the server’s error with a way to try again', async () => {
    const user = userEvent.setup();
    reportMock.mockRejectedValueOnce(new Error('Another period of activity is being loaded right now. Try again in a few seconds.'));
    renderWithProvider(<ActivityAnalysisPage />);

    expect(await screen.findByText(/Another period of activity is being loaded/)).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('95 matching people')).toBeVisible();
    expect(reportMock).toHaveBeenCalledTimes(2);
  });
});

describe('ActivityAnalysisPage filters', () => {
  it('applies licence and activity ranges only when asked, durations converted to seconds', async () => {
    const user = userEvent.setup();
    renderWithProvider(<ActivityAnalysisPage />);
    await screen.findByText('95 matching people');

    await user.click(screen.getByRole('button', { name: 'Filters' }));
    const panel = screen.getByRole('region', { name: 'Filters' });

    await user.type(within(panel).getByRole('spinbutton', { name: 'Minimum Teams calls' }), '5');
    await user.click(within(panel).getByRole('checkbox', { name: /Microsoft 365 E3/ }));
    await user.click(within(panel).getByRole('switch', { name: 'Show every metric' }));
    await user.type(within(panel).getByRole('spinbutton', { name: 'Maximum Teams audio time (hours)' }), '1.5');
    expect(within(panel).getByRole('spinbutton', { name: 'Maximum Teams calls' })).toHaveAttribute('placeholder', 'Up to 412');

    // Nothing is asked for until the filters are applied.
    expect(reportMock).toHaveBeenCalledTimes(1);
    expect(within(panel).getByText('Your changes are not applied yet.')).toBeVisible();

    await user.click(within(panel).getByRole('button', { name: 'Apply filters' }));
    await waitFor(() => expect(reportMock).toHaveBeenCalledTimes(2));
    expect(lastQuery()).toMatchObject({
      licences: [7],
      ranges: [
        { metric: 'teams.calls', min: 5, max: null },
        { metric: 'teams.audioDuration', min: null, max: 5400 },
      ],
      userFilter: null,
    });

    expect(screen.getByRole('button', { name: 'Filters (3)' })).toBeVisible();
    // Applying ends the edit: the panel closes and the focus returns to the button that opened it.
    expect(screen.queryByRole('region', { name: 'Filters' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Filters (3)' })).toHaveFocus();
    expect(screen.getByText('Licence: Microsoft 365 E3')).toBeVisible();
    expect(screen.getByText('Teams calls: at least 5')).toBeVisible();
    expect(screen.getByText('Teams audio time: at most 1.5 h')).toBeVisible();
  });

  it('refuses a range that cannot be applied, and says why', async () => {
    const user = userEvent.setup();
    renderWithProvider(<ActivityAnalysisPage />);
    await screen.findByText('95 matching people');

    await user.click(screen.getByRole('button', { name: 'Filters' }));
    const panel = screen.getByRole('region', { name: 'Filters' });
    await user.type(within(panel).getByRole('spinbutton', { name: 'Minimum Teams calls' }), '10');
    await user.type(within(panel).getByRole('spinbutton', { name: 'Maximum Teams calls' }), '3');

    expect(within(panel).getByText('The minimum is above the maximum.')).toBeVisible();
    expect(within(panel).getByRole('button', { name: 'Apply filters' })).toBeDisabled();
  });

  it('sends the people filter built in the filter bar', async () => {
    const user = userEvent.setup();
    renderWithProvider(<ActivityAnalysisPage />);
    await screen.findByText('95 matching people');

    await user.click(screen.getByRole('button', { name: 'Filters' }));
    const panel = screen.getByRole('region', { name: 'Filters' });
    const add = await within(panel).findByRole('button', { name: 'Add filter' });
    await waitFor(() => expect(add).toBeEnabled());
    await user.click(add);
    await user.click(await screen.findByRole('option', { name: /Department/ }));
    await user.click(screen.getByRole('combobox', { name: 'Values' }));
    await user.click(await screen.findByRole('option', { name: /Sales/ }));
    await user.click(within(panel).getByRole('button', { name: 'Apply' }));

    expect(reportMock).toHaveBeenCalledTimes(1);
    await user.click(within(panel).getByRole('button', { name: 'Apply filters' }));

    await waitFor(() => expect(reportMock).toHaveBeenCalledTimes(2));
    expect(lastQuery().userFilter).toBe('[{"d":"department","v":["Sales"]}]');
    expect(screen.getByRole('button', { name: 'Filters (1)' })).toBeVisible();
  });

  it('resets the draft without asking for anything until applied', async () => {
    const user = userEvent.setup();
    renderWithProvider(<ActivityAnalysisPage />);
    await screen.findByText('95 matching people');

    await user.click(screen.getByRole('button', { name: 'Filters' }));
    let panel = screen.getByRole('region', { name: 'Filters' });
    await user.click(within(panel).getByRole('checkbox', { name: /Microsoft 365 E5/ }));
    await user.click(within(panel).getByRole('button', { name: 'Apply filters' }));
    await waitFor(() => expect(reportMock).toHaveBeenCalledTimes(2));
    expect(lastQuery().licences).toEqual([9]);

    // The panel reopens on the filters in force.
    await user.click(screen.getByRole('button', { name: 'Filters (1)' }));
    panel = screen.getByRole('region', { name: 'Filters' });
    expect(within(panel).getByRole('checkbox', { name: /Microsoft 365 E5/ })).toBeChecked();

    await user.click(within(panel).getByRole('button', { name: 'Reset' }));
    expect(within(panel).getByRole('checkbox', { name: /Microsoft 365 E5/ })).not.toBeChecked();
    expect(reportMock).toHaveBeenCalledTimes(2);

    await user.click(within(panel).getByRole('button', { name: 'Apply filters' }));
    await waitFor(() => expect(reportMock).toHaveBeenCalledTimes(3));
    expect(lastQuery().licences).toEqual([]);
  });
});

describe('ActivityAnalysisPage people', () => {
  it('shows the groups the server folded for a reader without See PII as one row that cannot be expanded', async () => {
    reportMock.mockImplementation(async (query) => {
      const base = report(query);
      return {
        ...base,
        byDepartment: { rows: [...base.byDepartment.rows.slice(0, 2), { name: null, other: true, activePeople: 9 }], otherGroups: 3 },
        departments: [
          base.departments[0],
          base.departments[1],
          { name: null, other: true, people: 9, values: base.departments[2].values },
        ],
        otherDepartments: 3,
      };
    });
    renderWithProvider(<ActivityAnalysisPage />, { access: NO_PII });
    await screen.findByText('95 matching people');

    const matrix = screen.getByRole('region', { name: 'Results by department' });
    const other = within(matrix).getByRole('rowheader', { name: /Other groups \(fewer than 5 people each\)/ });
    expect(within(other).getByText('3 departments')).toBeInTheDocument();
    expect(within(other).queryByRole('button')).not.toBeInTheDocument();
    expect(screen.getByText('3 smaller groups are combined into “Other”.')).toBeVisible();
    expect(peopleMock).not.toHaveBeenCalled();
  });

  it('never asks a reader without See PII for named people', async () => {
    const user = userEvent.setup();
    renderWithProvider(<ActivityAnalysisPage />, { access: NO_PII });
    await screen.findByText('95 matching people');

    const matrix = screen.getByRole('region', { name: 'Results by department' });
    expect(within(matrix).queryByRole('button', { name: /Show the people in/ })).not.toBeInTheDocument();
    expect(screen.queryByText('Top people')).not.toBeInTheDocument();
    expect(screen.getByText('Individual details are hidden')).toBeVisible();
    expect(screen.getByText(/can expand each department to see its people/)).toBeVisible();

    // The filter panel has no people filter for them - its value lists name people - and so never
    // reads the attribute list, which the server would refuse.
    await user.click(screen.getByRole('button', { name: 'Filters' }));
    const panel = screen.getByRole('region', { name: 'Filters' });
    expect(within(panel).getByText(/needs the Portal.SeePII app role/)).toBeVisible();
    expect(within(panel).queryByRole('button', { name: 'Add filter' })).not.toBeInTheDocument();

    expect(peopleMock).not.toHaveBeenCalled();
    expect(fetchUserFilterDimensions).not.toHaveBeenCalled();
  });

  it('lets a reader with See PII expand a department to its people', async () => {
    const user = userEvent.setup();
    renderWithProvider(<ActivityAnalysisPage />);
    await screen.findByText('95 matching people');
    const matrix = screen.getByRole('region', { name: 'Results by department' });

    const expand = within(matrix).getByRole('button', { name: 'Show the people in Sales' });
    expect(expand).toHaveAttribute('aria-expanded', 'false');
    await user.click(expand);

    expect(await within(matrix).findByText('user1@contoso.com')).toBeInTheDocument();
    expect(within(matrix).getByText('user2@contoso.com')).toBeInTheDocument();
    expect(within(matrix).getByText('Showing 2 of 40 people, most active in Teams private chats first.')).toBeInTheDocument();
    expect(within(matrix).getByRole('button', { name: 'Hide the people in Sales' })).toHaveAttribute('aria-expanded', 'true');

    expect(peopleMock).toHaveBeenCalledWith(
      expect.objectContaining({
        from: '2025-10-06',
        to: '2026-09-28',
        metrics: TEAMS_CORE,
        department: 'Sales',
        noDepartment: false,
        sort: 'teams.privateChats',
        top: 100,
      }),
      expect.anything(),
    );

    await user.click(within(matrix).getByRole('button', { name: 'Show the people in (no department)' }));
    expect(await within(matrix).findByText('user9@fabrikam.com')).toBeInTheDocument();
    expect(peopleMock).toHaveBeenCalledWith(expect.objectContaining({ noDepartment: true }), expect.anything());
  });

  it('lists the most active people for a reader with See PII', async () => {
    renderWithProvider(<ActivityAnalysisPage />);
    expect(await screen.findByText('Top people')).toBeVisible();
    expect(await screen.findByText('champion@contoso.com')).toBeVisible();
    expect(peopleMock).toHaveBeenCalledWith(
      expect.objectContaining({ sort: 'teams.privateChats', top: 10, metrics: TEAMS_CORE }),
      expect.anything(),
    );
    const topCall = peopleMock.mock.calls.find(([query]) => query.top === 10)![0];
    expect(topCall.department).toBeUndefined();
    expect(topCall.noDepartment).toBeUndefined();
  });
});

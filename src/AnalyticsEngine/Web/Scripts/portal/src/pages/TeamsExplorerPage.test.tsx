import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, fireEvent, waitFor } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import TeamsExplorerPage from './TeamsExplorerPage';
import type {
  TeamsAvailability,
  TeamsCollaboration,
  TeamsMeetings,
  TeamsOverview,
  TeamsWindow,
} from '../types/teamsExplorer';

const mockAvailability = vi.fn();
const mockOverview = vi.fn();
const mockAdoption = vi.fn();
const mockMeetings = vi.fn();
const mockCollaboration = vi.fn();
const mockConversations = vi.fn();
const mockPeople = vi.fn();

vi.mock('../api/teamsExplorerApi', () => ({
  fetchTeamsAvailability: (...args: unknown[]) => mockAvailability(...args),
  fetchTeamsOverview: (...args: unknown[]) => mockOverview(...args),
  fetchTeamsAdoption: (...args: unknown[]) => mockAdoption(...args),
  fetchTeamsMeetings: (...args: unknown[]) => mockMeetings(...args),
  fetchTeamsCollaboration: (...args: unknown[]) => mockCollaboration(...args),
  fetchTeamsConversations: (...args: unknown[]) => mockConversations(...args),
  fetchTeamsPeople: (...args: unknown[]) => mockPeople(...args),
  downloadTeamsExport: vi.fn(),
}));

const window28: TeamsWindow = {
  days: 28,
  fromUtc: '2026-02-21T00:00:00Z',
  toUtc: '2026-03-20T00:00:00Z',
  usageFromUtc: '2026-02-18T00:00:00Z',
  usageToUtc: '2026-03-17T00:00:00Z',
  workingDays: 20,
};

const availability = (over: Partial<TeamsAvailability> = {}): TeamsAvailability => ({
  usageReportsAvailable: true,
  callsAvailable: true,
  teamsAnalyticsAvailable: true,
  cognitiveAvailable: true,
  userMetadataAvailable: true,
  authorisedTeams: 3,
  totalTeams: 5,
  available: true,
  reasons: [],
  ...over,
});

const overview = (over: Partial<TeamsOverview> = {}): TeamsOverview => ({
  window: window28,
  queries: [{ key: 'overview-usage', sql: 'SELECT 1', error: null, elapsedMs: 12 }],
  kpis: {
    knownUsers: 1000,
    activeUsers: 620,
    reachPct: 62,
    channelMessages: 4000,
    privateMessages: 6000,
    openCollaborationPct: 40,
    meetingsAttended: 5000,
    meetingsOrganised: 900,
    meetingsPerActiveUser: 8.1,
    audioHours: 1200,
    videoSharePct: 45,
    screenShareSharePct: 20,
    calls: 3200,
    totalTeams: 5,
    activeTeams: 4,
    totalChannels: 20,
    activeChannels: 12,
  },
  trend: [],
  segmentMix: [
    { segment: 'Power', label: 'Power', description: 'Most working days.', users: 100, sharePct: 20 },
    { segment: 'Regular', label: 'Regular', description: 'Some working days.', users: 200, sharePct: 40 },
    { segment: 'Light', label: 'Light', description: 'Few working days.', users: 150, sharePct: 30 },
    { segment: 'Dormant', label: 'Dormant', description: 'None.', users: 50, sharePct: 10 },
  ],
  judgements: [
    {
      key: 'reach',
      tone: 'warning',
      headline: '62% of known users used Teams in this period.',
      detail: '620 of 1,000 directory users.',
    },
  ],
  ...over,
});

const meetings = (over: Partial<TeamsMeetings> = {}): TeamsMeetings => ({
  window: window28,
  queries: [],
  workingDayStartHour: 8,
  workingDayEndHour: 18,
  kpis: {
    calls: 3200,
    groupCalls: 1200,
    peerToPeerCalls: 2000,
    attendees: 600,
    callHours: 900,
    attendeeHours: 2400,
    meanDurationMinutes: 17,
    meanAttendees: 2.6,
    afterHoursPct: 8,
    weekendPct: 2,
    organiserConcentrationPct: 35,
    attendeeEngagementPct: 88,
  },
  trend: [],
  sizeDistribution: [],
  durationDistribution: [],
  heatmap: [],
  periodOfDay: [],
  modalityMix: [],
  topOrganisers: [],
  topAttendees: [],
  quality: { feedbackCount: 0, failureCount: 0, ratings: [], failureReasons: [], failureStages: [] },
  ...over,
});

const collaboration = (over: Partial<TeamsCollaboration> = {}): TeamsCollaboration => ({
  window: window28,
  queries: [],
  kpis: {
    totalTeams: 5,
    activeTeams: 4,
    dormantTeams: 1,
    ownerlessTeams: 2,
    authorisedTeams: 3,
    totalChannels: 20,
    activeChannels: 12,
    channelMessages: 4000,
    reactions: 900,
  },
  teams: [],
  channels: [],
  ownerlessTeams: [],
  dormantTeams: [],
  reactionMix: [],
  tabUsage: [],
  membershipTrend: [],
  ...over,
});

beforeEach(() => {
  vi.clearAllMocks();
  mockAvailability.mockResolvedValue(availability());
  mockOverview.mockResolvedValue(overview());
  mockMeetings.mockResolvedValue(meetings());
  mockCollaboration.mockResolvedValue(collaboration());
});

describe('TeamsExplorerPage', () => {
  it('opens on the overview and loads only that tab', async () => {
    renderWithProvider(<TeamsExplorerPage />);

    expect(await screen.findByText('Teams reach')).toBeInTheDocument();
    expect(mockOverview).toHaveBeenCalledTimes(1);

    // The other five tabs must not be fetched until they are opened - otherwise opening the page
    // costs six multi-query round trips.
    expect(mockMeetings).not.toHaveBeenCalled();
    expect(mockCollaboration).not.toHaveBeenCalled();
    expect(mockPeople).not.toHaveBeenCalled();
  });

  it('leads with the written findings, not just the charts', async () => {
    renderWithProvider(<TeamsExplorerPage />);

    expect(
      await screen.findByText('62% of known users used Teams in this period.'),
    ).toBeInTheDocument();
  });

  it('loads a tab when it is first opened', async () => {
    renderWithProvider(<TeamsExplorerPage />);
    await screen.findByText('Teams reach');

    fireEvent.click(screen.getByRole('tab', { name: 'Meetings & calls' }));

    await waitFor(() => expect(mockMeetings).toHaveBeenCalledTimes(1));
    expect(await screen.findByText('Attendee hours')).toBeInTheDocument();
  });

  it('renders call failure reasons as server-authored reasons, not quality rating codes', async () => {
    mockMeetings.mockResolvedValue(meetings({
      quality: {
        feedbackCount: 1,
        failureCount: 2,
        ratings: [{ key: 'poor', label: 'Poor', count: 1, sharePct: 100 }],
        failureReasons: [{ key: 'poor', label: 'Poor network path', count: 2, sharePct: 100 }],
        failureStages: [],
      },
    }));

    renderWithProvider(<TeamsExplorerPage />);
    await screen.findByText('Teams reach');
    fireEvent.click(screen.getByRole('tab', { name: 'Meetings & calls' }));

    expect(await screen.findByText('Poor network path')).toBeInTheDocument();
    expect(screen.getByText('Poor')).toBeInTheDocument();
  });

  it('refetches the visible tab when the period changes', async () => {
    renderWithProvider(<TeamsExplorerPage />);
    await screen.findByText('Teams reach');

    fireEvent.change(screen.getByLabelText('Reporting period'), { target: { value: '90' } });

    await waitFor(() => expect(mockOverview).toHaveBeenCalledTimes(2));
    expect(mockOverview).toHaveBeenLastCalledWith(90, expect.anything());
  });

  it('names every missing data source rather than hiding the tabs', async () => {
    mockAvailability.mockResolvedValue(
      availability({
        callsAvailable: false,
        cognitiveAvailable: false,
        reasons: [
          'The Teams calls import is switched off, so there are no call records.',
          'Cognitive services are not configured, so channel messages are not scored.',
        ],
      }),
    );

    renderWithProvider(<TeamsExplorerPage />);

    const toggle = await screen.findByRole('button', { name: /Show what is missing \(2\)/ });
    fireEvent.click(toggle);

    expect(
      await screen.findByText(/The Teams calls import is switched off/),
    ).toBeInTheDocument();

    // Every tab is still reachable - a hidden tab tells an admin nothing.
    expect(screen.getByRole('tab', { name: 'Meetings & calls' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Conversation insights' })).toBeInTheDocument();
  });

  it('explains an unavailable tab instead of showing zeros', async () => {
    mockAvailability.mockResolvedValue(availability({ callsAvailable: false, reasons: ['off'] }));
    renderWithProvider(<TeamsExplorerPage />);
    await screen.findByText('Teams reach');

    fireEvent.click(screen.getByRole('tab', { name: 'Meetings & calls' }));

    expect(
      await screen.findByText(/The Teams calls import is switched off, so there are no call records to analyse/),
    ).toBeInTheDocument();
  });

  it('surfaces a load failure instead of an empty page', async () => {
    mockOverview.mockRejectedValue(new Error("Couldn't load the Teams overview (500)."));

    renderWithProvider(<TeamsExplorerPage />);

    expect(
      await screen.findByText("Couldn't load the Teams overview (500)."),
    ).toBeInTheDocument();
  });

  it('says so when no Teams import is switched on at all', async () => {
    mockAvailability.mockResolvedValue(
      availability({
        usageReportsAvailable: false,
        callsAvailable: false,
        teamsAnalyticsAvailable: false,
        available: false,
        reasons: ['everything is off'],
      }),
    );

    renderWithProvider(<TeamsExplorerPage />);

    expect(
      await screen.findByText(/None of the Teams imports are switched on/),
    ).toBeInTheDocument();
  });
});

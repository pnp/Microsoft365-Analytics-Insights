import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import AdoptionPanel from './AdoptionPanel';
import ConversationsPanel from './ConversationsPanel';
import MeetingsPanel from './MeetingsPanel';
import PeoplePanel from './PeoplePanel';
import type {
  TeamsAdoption,
  TeamsConversations,
  TeamsMeetings,
  TeamsPeople,
  TeamsWindow,
} from '../../types/teamsExplorer';

const window28: TeamsWindow = {
  days: 28,
  fromUtc: '2026-02-21T00:00:00Z',
  toUtc: '2026-03-20T00:00:00Z',
  usageFromUtc: '2026-02-18T00:00:00Z',
  usageToUtc: '2026-03-17T00:00:00Z',
  workingDays: 20,
};

const conversations = (over: Partial<TeamsConversations> = {}): TeamsConversations => ({
  window: window28,
  queries: [],
  cognitiveAvailable: true,
  scoredChannelDays: 2,
  keywords: [{ name: 'assembly line', count: 12, sharePct: 60 }],
  languages: [{ name: 'English', count: 4, sharePct: 100 }],
  sentimentTrend: [{ weekStart: '2026-03-02T00:00:00Z', sentiment: 0.5, messages: 40 }],
  sentimentByTeam: [{ name: 'Contoso Engineering', sentiment: 0.5, messages: 40 }],
  sentimentByChannel: [],
  ...over,
});

const people = (over: Partial<TeamsPeople> = {}): TeamsPeople => ({
  window: window28,
  queries: [],
  namesObfuscated: false,
  champions: [
    {
      userPrincipalName: 'ada@contoso.com',
      department: 'Engineering',
      activeDays: 18,
      channelMessages: 240,
      privateMessages: 90,
      meetingsOrganised: 12,
      meetingsAttended: 60,
      callsHosted: 8,
      callsAttended: 40,
      segment: 'Power',
      lastActivity: '2026-03-16T00:00:00Z',
    },
  ],
  dormant: [],
  championsByDepartment: [{ name: 'Engineering', count: 4, sharePct: 100 }],
  ...over,
});

const noop = () => undefined;

const adoption = (over: Partial<TeamsAdoption> = {}): TeamsAdoption => ({
  window: window28,
  queries: [],
  groupBy: 'jobTitle',
  rhythm: { meanDailyActiveUsers: 10, weeklyActiveUsers: 20, monthlyActiveUsers: 30, stickinessPct: 33 },
  segmentMix: [],
  segmentTrend: [],
  breakdown: [
    { name: 'Contoso Analysts', knownUsers: 12, activeUsers: 10, reachPct: 83.3, messagesPerActiveUser: 40, meetingsPerActiveUser: 2 },
    { name: null, other: true, knownUsers: 35, activeUsers: 7, reachPct: 20, messagesPerActiveUser: 150, meetingsPerActiveUser: 1 },
  ],
  devices: [],
  lifecycle: { newUsers: 1, returningUsers: 2, lapsedUsers: 3 },
  minimumGroupSize: 5,
  otherGroups: 3,
  ...over,
});

describe('AdoptionPanel', () => {
  it('names the row a reader without See PII gets in place of the smaller groups, and says why', () => {
    renderWithProvider(<AdoptionPanel data={adoption()} groupBy="jobTitle" onGroupByChange={noop} demographicsAvailable />);

    expect(screen.getAllByText('Contoso Analysts').length).toBeGreaterThan(0);
    expect(screen.getAllByText('3 smaller groups, combined').length).toBeGreaterThan(0);
    expect(
      screen.getByText(
        'Groups of fewer than 5 people, or with fewer than 5 active people, are combined into one row so that nobody can be singled out.',
      ),
    ).toBeInTheDocument();
  });

  it('says the smaller groups were left out, not combined, when even together they were too few to show', () => {
    // Groups of 1 to 4 active people that no shown group with active people could top up: the combined row is dropped.
    renderWithProvider(
      <AdoptionPanel
        data={adoption({ breakdown: adoption().breakdown.slice(0, 1), otherGroups: 2 })}
        groupBy="jobTitle"
        onGroupByChange={noop}
        demographicsAvailable
      />,
    );

    expect(
      screen.getByText(
        'Groups of fewer than 5 people, or with fewer than 5 active people, are left out: even together they are too few to show without singling someone out.',
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText(/combined into one row/)).not.toBeInTheDocument();
    expect(screen.queryByText(/smaller groups?, combined/)).not.toBeInTheDocument();
  });

  it('says the smaller groups were combined, not left out, when their row is shown', () => {
    renderWithProvider(<AdoptionPanel data={adoption()} groupBy="jobTitle" onGroupByChange={noop} demographicsAvailable />);

    expect(screen.queryByText(/are left out/)).not.toBeInTheDocument();
    expect(screen.getByText(/combined into one row/)).toBeInTheDocument();
  });

  it('explains an empty breakdown when every group is too small to show', () => {
    renderWithProvider(
      <AdoptionPanel data={adoption({ breakdown: [], otherGroups: 4 })} groupBy="jobTitle" onGroupByChange={noop} demographicsAvailable />,
    );

    expect(
      screen.getByText(
        'Every group has fewer than 5 people, or fewer than 5 active people, so this breakdown is hidden to protect their privacy.',
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText('No users could be grouped for this period.')).not.toBeInTheDocument();
  });

  it('says nothing about combined groups to a reader who sees every group', () => {
    renderWithProvider(
      <AdoptionPanel
        data={adoption({ breakdown: adoption().breakdown.slice(0, 1), otherGroups: 0, minimumGroupSize: null })}
        groupBy="jobTitle"
        onGroupByChange={noop}
        demographicsAvailable
      />,
    );

    expect(screen.queryByText(/combined into one row/)).not.toBeInTheDocument();
    expect(screen.queryByText(/smaller group/)).not.toBeInTheDocument();
  });
});

describe('ConversationsPanel', () => {
  it('explains the missing cognitive configuration instead of rendering empty charts', () => {
    renderWithProvider(<ConversationsPanel data={conversations({ cognitiveAvailable: false })} />);

    expect(screen.getByText(/Cognitive services are not configured/)).toBeInTheDocument();
    expect(screen.queryByText('assembly line')).not.toBeInTheDocument();
  });

  it('distinguishes "configured but nothing scored yet" from "not configured"', () => {
    renderWithProvider(
      <ConversationsPanel data={conversations({ scoredChannelDays: 0, sentimentByTeam: [] })} />,
    );

    expect(screen.getByText(/no channel day in this period carries a score yet/)).toBeInTheDocument();
  });

  it('always states the sentiment scale next to a sentiment figure', () => {
    const { container } = renderWithProvider(<ConversationsPanel data={conversations()} />);

    expect(screen.getAllByText(/0\.5 is neutral/).length).toBeGreaterThan(0);

    // The score is drawn as a traffic light rather than printed, so the exact figure has to be
    // reachable from the graphic itself - not only from the section's scale note.
    const lit = container.querySelector('[data-lamp="neutral"][data-lit="true"]');
    expect(lit).not.toBeNull();
    expect(lit?.closest('[title]')?.getAttribute('title')).toContain('Sentiment 0.50 (neutral)');
  });

  it('says why key phrases and languages are hidden from a reader without See PII, rather than reading as none', () => {
    renderWithProvider(
      <ConversationsPanel
        data={conversations({
          keywords: [],
          languages: [],
          keywordsWithheld: true,
          languagesWithheld: true,
          minimumGroupSize: 5,
          withheldSentimentTeams: 1,
        })}
      />,
    );

    expect(screen.getByText(/key phrases are taken from what people wrote/)).toBeInTheDocument();
    expect(screen.getByText(/nothing recorded says how many people write in each language/)).toBeInTheDocument();
    expect(screen.queryByText('No key phrases were extracted for this period.')).not.toBeInTheDocument();
    expect(screen.getByText(/Teams with fewer than 5 members are not listed so that nobody can be singled out/)).toBeInTheDocument();
  });

  it('says nothing is held back from a reader with See PII', () => {
    renderWithProvider(<ConversationsPanel data={conversations()} />);

    expect(screen.queryByText(/Hidden to protect privacy/)).not.toBeInTheDocument();
    expect(screen.queryByText(/are not listed/)).not.toBeInTheDocument();
  });
});

const meetings = (over: Partial<TeamsMeetings> = {}): TeamsMeetings => ({
  window: window28,
  queries: [],
  workingDayStartHour: 8,
  workingDayEndHour: 18,
  kpis: {
    calls: 6,
    groupCalls: 4,
    peerToPeerCalls: 2,
    attendees: 4,
    callHours: 3,
    attendeeHours: 9,
    meanDurationMinutes: 30,
    meanAttendees: 3,
    afterHoursPct: 0,
    weekendPct: 0,
    organiserConcentrationPct: 50,
    attendeeEngagementPct: 80,
  },
  trend: [{ weekStart: '2026-03-02T00:00:00Z', calls: 6, minutes: 180, attendees: 4 }],
  sizeDistribution: [{ key: '3-5', label: '3-5', count: 6, sharePct: 100 }],
  durationDistribution: [{ key: '15-30', label: '15-30 minutes', count: 6, sharePct: 100 }],
  heatmap: [{ dayOfWeek: 1, hour: 10, calls: 6 }],
  periodOfDay: [{ key: 'late-morning', label: 'Late morning (09:00-12:00)', count: 6, sharePct: 100 }],
  modalityMix: [{ key: 'audio', label: 'audio', count: 6, sharePct: 100 }],
  topOrganisers: [],
  topAttendees: [],
  quality: { feedbackCount: 0, failureCount: 0, ratings: [], failureReasons: [], failureStages: [] },
  ...over,
});

describe('MeetingsPanel', () => {
  it('says the distributions are withheld when too few people attended, and that a small week is left blank', () => {
    renderWithProvider(
      <MeetingsPanel
        data={meetings({
          minimumGroupSize: 5,
          distributionsWithheld: true,
          sizeDistribution: [],
          durationDistribution: [],
          heatmap: [],
          periodOfDay: [],
          modalityMix: [],
          trend: [{ weekStart: '2026-03-02T00:00:00Z', calls: null, minutes: null, attendees: null, withheld: true }],
        })}
        callsAvailable
        showPii={false}
      />,
    );

    // Heatmap, size, length, time of day, modalities and call experience each say why they are empty.
    expect(screen.getAllByText(/Fewer than 5 people attended the calls in this view/)).toHaveLength(6);
    expect(screen.getAllByText(/Weeks in which fewer than 5 people attended calls are left blank/)).toHaveLength(2);
    expect(screen.queryByText(/No call feedback or failures were recorded/)).not.toBeInTheDocument();
  });

  it('names the combined slice of modalities few people used, and says why', () => {
    renderWithProvider(
      <MeetingsPanel
        data={meetings({
          minimumGroupSize: 5,
          kpis: { ...meetings().kpis, attendees: 12 },
          modalityMix: [
            { key: 'audio', label: 'audio', count: 50, sharePct: 69 },
            { key: null, label: null, count: 22, sharePct: 31, other: true },
          ],
          otherModalities: 2,
        })}
        callsAvailable
        showPii={false}
      />,
    );

    expect(screen.getByText(/Modalities used by fewer than 5 people are combined into one slice/)).toBeInTheDocument();
    expect(screen.queryByText(/Fewer than 5 people attended the calls in this view/)).not.toBeInTheDocument();
  });
});

describe('PeoplePanel', () => {
  it('explains the missing usage reports rather than ranking nobody', () => {
    renderWithProvider(
      <PeoplePanel
        data={people()}
        usageReportsAvailable={false}
        onExportChampions={noop}
        onExportDormant={noop}
        exporting={false}
        showPii
      />,
    );

    expect(screen.getByText(/usage reports import is switched off/)).toBeInTheDocument();
    expect(screen.queryByText('ada@contoso.com')).not.toBeInTheDocument();
  });

  it('names people and says what the list is for', () => {
    renderWithProvider(
      <PeoplePanel
        data={people()}
        usageReportsAvailable
        onExportChampions={noop}
        onExportDormant={noop}
        exporting={false}
        showPii
      />,
    );

    expect(screen.getByText('ada@contoso.com')).toBeInTheDocument();
    expect(screen.getByText('Power')).toBeInTheDocument();
    expect(screen.getByText(/not for performance management/)).toBeInTheDocument();
  });

  it('says why nobody is listed as dormant when the period was not imported in full', () => {
    renderWithProvider(
      <PeoplePanel
        data={people({
          dormancy: {
            known: false,
            reason: 'incompleteImport',
            expectedDays: 28,
            importedDays: 26,
            teamsLicensedUsers: 40,
            unmeasuredUsers: 0,
          },
        })}
        usageReportsAvailable
        onExportChampions={noop}
        onExportDormant={noop}
        exporting={false}
        showPii
      />,
    );

    expect(screen.getByText(/were imported for 26 of its 28 days/)).toBeInTheDocument();
    expect(screen.queryByText('Everyone licensed for Teams did something in Teams this period.')).not.toBeInTheDocument();
  });

  it('keeps the plain empty message when dormancy was measured in full', () => {
    renderWithProvider(
      <PeoplePanel
        data={people({
          dormancy: { known: true, reason: null, expectedDays: 28, importedDays: 28, teamsLicensedUsers: 40, unmeasuredUsers: 0 },
        })}
        usageReportsAvailable
        onExportChampions={noop}
        onExportDormant={noop}
        exporting={false}
        showPii
      />,
    );

    expect(screen.getByText('Everyone licensed for Teams did something in Teams this period.')).toBeInTheDocument();
    expect(screen.queryByText(/Dormant people cannot be identified/)).not.toBeInTheDocument();
  });

  it('explains anonymised usage reports as a tenant setting, not a product fault', () => {
    renderWithProvider(
      <PeoplePanel
        data={people({ namesObfuscated: true })}
        usageReportsAvailable
        onExportChampions={noop}
        onExportDormant={noop}
        exporting={false}
        showPii
      />,
    );

    expect(screen.getByText(/anonymised user names/)).toBeInTheDocument();
    expect(screen.getByText(/admin centre/)).toBeInTheDocument();
  });

  it('names the row of departments combined for a reader without See PII, and says why', () => {
    renderWithProvider(
      <PeoplePanel
        data={people({
          champions: [],
          championsByDepartment: [
            { name: 'Engineering', count: 9, sharePct: 64.3 },
            { name: null, other: true, count: 5, sharePct: 35.7 },
          ],
          minimumGroupSize: 5,
          otherChampionDepartments: 2,
        })}
        usageReportsAvailable
        onExportChampions={noop}
        onExportDormant={noop}
        exporting={false}
        showPii={false}
      />,
    );

    expect(screen.getAllByText('Engineering').length).toBeGreaterThan(0);
    expect(screen.getAllByText('2 smaller departments, combined').length).toBeGreaterThan(0);
    expect(screen.getByText(/Departments with fewer than 5 power users are combined into one row/)).toBeInTheDocument();
  });

  it('explains an empty chart when every department has too few power users to show', () => {
    renderWithProvider(
      <PeoplePanel
        data={people({ champions: [], championsByDepartment: [], minimumGroupSize: 5, otherChampionDepartments: 3 })}
        usageReportsAvailable
        onExportChampions={noop}
        onExportDormant={noop}
        exporting={false}
        showPii={false}
      />,
    );

    expect(
      screen.getByText('Every department has fewer than 5 power users, so this chart is hidden to protect their privacy.'),
    ).toBeInTheDocument();
  });
});

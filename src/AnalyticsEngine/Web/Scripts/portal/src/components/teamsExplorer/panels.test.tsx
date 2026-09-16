import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import ConversationsPanel from './ConversationsPanel';
import PeoplePanel from './PeoplePanel';
import type {
  TeamsConversations,
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
    renderWithProvider(<ConversationsPanel data={conversations()} />);

    expect(screen.getAllByText(/0\.5 is neutral/).length).toBeGreaterThan(0);
    expect(screen.getByText('0.50 (neutral)')).toBeInTheDocument();
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
      />,
    );

    expect(screen.getByText('ada@contoso.com')).toBeInTheDocument();
    expect(screen.getByText('Power')).toBeInTheDocument();
    expect(screen.getByText(/not for performance management/)).toBeInTheDocument();
  });

  it('explains anonymised usage reports as a tenant setting, not a product fault', () => {
    renderWithProvider(
      <PeoplePanel
        data={people({ namesObfuscated: true })}
        usageReportsAvailable
        onExportChampions={noop}
        onExportDormant={noop}
        exporting={false}
      />,
    );

    expect(screen.getByText(/anonymised user names/)).toBeInTheDocument();
    expect(screen.getByText(/admin centre/)).toBeInTheDocument();
  });
});

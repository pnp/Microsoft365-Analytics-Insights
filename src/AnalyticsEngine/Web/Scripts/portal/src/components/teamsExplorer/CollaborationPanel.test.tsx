import { describe, it, expect, vi } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import CollaborationPanel from './CollaborationPanel';
import type { TeamsCollaboration, TeamsWindow } from '../../types/teamsExplorer';

const window28: TeamsWindow = {
  days: 28,
  fromUtc: '2026-02-21T00:00:00Z',
  toUtc: '2026-03-20T00:00:00Z',
  usageFromUtc: '2026-02-18T00:00:00Z',
  usageToUtc: '2026-03-17T00:00:00Z',
  workingDays: 20,
};

const data = (over: Partial<TeamsCollaboration> = {}): TeamsCollaboration => ({
  window: window28,
  queries: [],
  kpis: {
    totalTeams: 3,
    activeTeams: 1,
    dormantTeams: 1,
    ownerlessTeams: 2,
    authorisedTeams: 2,
    totalChannels: 4,
    activeChannels: 1,
    channelMessages: 40,
    reactions: 6,
  },
  teams: [
    {
      id: 1,
      name: 'Contoso Engineering',
      members: 12,
      owners: 1,
      channels: 3,
      tabs: 2,
      messages: 40,
      reactions: 6,
      sentiment: 0.75,
      activeDays: 9,
      authorised: true,
    },
    {
      id: 2,
      name: 'Contoso Unauthorised',
      members: 0,
      owners: 0,
      channels: 1,
      tabs: 0,
      messages: 0,
      reactions: 0,
      sentiment: null,
      activeDays: 0,
      authorised: false,
    },
  ],
  channels: [],
  ownerlessTeams: [{ name: 'Contoso Unauthorised', count: 50, sharePct: null }],
  dormantTeams: [{ name: 'Contoso Dormant', count: 1, sharePct: null }],
  reactionMix: [],
  tabUsage: [],
  membershipTrend: [],
  ...over,
});

const noop = () => undefined;

describe('CollaborationPanel', () => {
  it('explains the import rather than showing an empty leaderboard when it is off', () => {
    renderWithProvider(
      <CollaborationPanel
        data={data()}
        analyticsAvailable={false}
        authorisedTeams={0}
        onExportTeams={noop}
        onExportChannels={noop}
        exporting={false}
      />,
    );

    expect(screen.getByText(/Teams deep analytics is switched off/)).toBeInTheDocument();
    expect(screen.queryByText('Contoso Engineering')).not.toBeInTheDocument();
  });

  it('warns when no team has been authorised, because nothing can ever appear', () => {
    renderWithProvider(
      <CollaborationPanel
        data={data()}
        analyticsAvailable
        authorisedTeams={0}
        onExportTeams={noop}
        onExportChannels={noop}
        exporting={false}
      />,
    );

    expect(screen.getByText(/No team has been authorised for deep analytics/)).toBeInTheDocument();
  });

  it('marks an unauthorised team as not measured rather than letting its zero read as quiet', () => {
    renderWithProvider(
      <CollaborationPanel
        data={data()}
        analyticsAvailable
        authorisedTeams={2}
        onExportTeams={noop}
        onExportChannels={noop}
        exporting={false}
      />,
    );

    expect(screen.getByText('not measured')).toBeInTheDocument();
    expect(screen.getAllByText('ownerless').length).toBeGreaterThan(0);
  });

  it('renders sentiment on its own scale, never as a percentage', () => {
    renderWithProvider(
      <CollaborationPanel
        data={data()}
        analyticsAvailable
        authorisedTeams={2}
        onExportTeams={noop}
        onExportChannels={noop}
        exporting={false}
      />,
    );

    expect(screen.getByText('0.75 (positive)')).toBeInTheDocument();
    expect(screen.queryByText('75%')).not.toBeInTheDocument();
  });

  it('states that dormant only covers authorised teams', () => {
    renderWithProvider(
      <CollaborationPanel
        data={data()}
        analyticsAvailable
        authorisedTeams={2}
        onExportTeams={noop}
        onExportChannels={noop}
        exporting={false}
      />,
    );

    expect(screen.getByText(/An unauthorised team is unmeasured, not quiet/)).toBeInTheDocument();
  });

  it('exports the leaderboard on request', () => {
    const onExportTeams = vi.fn();

    renderWithProvider(
      <CollaborationPanel
        data={data()}
        analyticsAvailable
        authorisedTeams={2}
        onExportTeams={onExportTeams}
        onExportChannels={noop}
        exporting={false}
      />,
    );

    screen.getAllByRole('button', { name: 'Export CSV' })[0].click();
    expect(onExportTeams).toHaveBeenCalled();
  });
});

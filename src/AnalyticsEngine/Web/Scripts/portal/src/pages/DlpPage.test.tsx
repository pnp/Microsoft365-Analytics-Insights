import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, fireEvent } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import DlpPage from './DlpPage';
import type { DlpAvailability, DlpSummary } from '../types/dlp';

const mockAvailability = vi.fn();
const mockSummary = vi.fn();

vi.mock('../api/dlpApi', () => ({
  fetchDlpAvailability: (...args: unknown[]) => mockAvailability(...args),
  fetchDlpSummary: (...args: unknown[]) => mockSummary(...args),
}));

const availability = (over: Partial<DlpAvailability> = {}): DlpAvailability => ({
  copilotDlpAvailable: true,
  tenantDlpAvailable: true,
  available: true,
  reasons: [],
  ...over,
});

const summary = (over: Partial<DlpSummary> = {}): DlpSummary => ({
  fromUtc: '2026-08-12T00:00:00Z',
  toUtc: '2026-09-09T00:00:00Z',
  copilotBlockedCount: 12,
  copilotAuditedCount: 5,
  usersImpacted: 7,
  agentsImpacted: 3,
  policiesInvolved: 2,
  topAgents: [
    {
      id: 'agent-1',
      name: 'Contoso HR Agent',
      blockedCount: 8,
      auditedCount: 1,
      usersAffected: 4,
      totalCount: 9,
      policies: [
        { id: 'pol-1', name: 'Block Copilot on Confidential', blockedCount: 6, auditedCount: 0, usersAffected: null, totalCount: 6 },
        { id: 'pol-2', name: 'Payment card data', blockedCount: 2, auditedCount: 1, usersAffected: null, totalCount: 3 },
      ],
    },
  ],
  topUsers: [
    {
      id: '1',
      name: 'ada@contoso.com',
      blockedCount: 4,
      auditedCount: 0,
      usersAffected: null,
      totalCount: 4,
      policies: [
        { id: 'pol-1', name: 'HR records policy', blockedCount: 3, auditedCount: 0, usersAffected: null, totalCount: 3 },
      ],
    },
  ],
  topPolicies: [
    { id: 'pol-1', name: 'Block Copilot on Confidential', blockedCount: 9, auditedCount: 2, usersAffected: 5, totalCount: 11 },
  ],
  topSensitivityLabels: [],
  trend: [],
  tenantTopPolicies: [],
  tenantBlockedCount: 3,
  tenantAuditedCount: 1,
  ...over,
});

beforeEach(() => {
  vi.clearAllMocks();
  mockAvailability.mockResolvedValue(availability());
  mockSummary.mockResolvedValue(summary());
});

describe('DlpPage', () => {
  it('shows the blocked/audited split, the agents that were affected, and the policies responsible', async () => {
    renderWithProvider(<DlpPage />);

    expect(await screen.findByText('Contoso HR Agent')).toBeInTheDocument();
    expect(screen.getByText('Block Copilot on Confidential')).toBeInTheDocument();
    expect(screen.getByText('ada@contoso.com')).toBeInTheDocument();

    // Blocked and audited-only are separate columns on purpose: merging them would claim users were
    // denied content when a policy was merely running in simulation.
    expect(screen.getAllByText('Blocked').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Audited only').length).toBeGreaterThan(0);
  });

  /**
   * Regression test for the reported crash:
   *   "Uncaught TypeError: Cannot read properties of undefined (reading 'map')"
   *
   * Root cause was a serialisation contract mismatch - the API returned PascalCase, so every field
   * the page read was undefined and the unguarded .map() calls took the whole page down. The C#
   * contract test pins the real fix; this pins the UI's behaviour if a payload ever drifts again:
   * the page must degrade, not white-screen.
   */
  it('does not crash when the API omits the ranked lists and reasons', async () => {
    mockAvailability.mockResolvedValue({ copilotDlpAvailable: true, tenantDlpAvailable: true, available: true } as DlpAvailability);
    mockSummary.mockResolvedValue({
      fromUtc: '2026-08-12T00:00:00Z',
      toUtc: '2026-09-09T00:00:00Z',
      copilotBlockedCount: 0,
      copilotAuditedCount: 0,
      usersImpacted: 0,
      agentsImpacted: 0,
      policiesInvolved: 0,
      tenantBlockedCount: 0,
      tenantAuditedCount: 0,
    } as DlpSummary);

    renderWithProvider(<DlpPage />);

    expect(await screen.findByText('Who and what is affected')).toBeInTheDocument();
    expect(screen.getAllByText('Nothing in this period.').length).toBeGreaterThan(0);
  });

  it('explains what to switch on when a DLP source is disabled', async () => {
    mockAvailability.mockResolvedValue(
      availability({
        tenantDlpAvailable: false,
        reasons: ['The Data Loss Prevention import (DLP.All) is switched off.'],
      }),
    );

    renderWithProvider(<DlpPage />);

    expect(await screen.findByText(/Data Loss Prevention import \(DLP.All\) is switched off/)).toBeInTheDocument();
    expect(screen.getByText(/The DLP import is switched off, so there is nothing to show here./)).toBeInTheDocument();
  });

  it('surfaces an availability failure instead of rendering an empty report', async () => {
    mockAvailability.mockRejectedValue(new Error('availability boom'));
    renderWithProvider(<DlpPage />);
    expect(await screen.findByText('availability boom')).toBeInTheDocument();
  });

  it('reveals which policies affected a given agent when that agent is selected', async () => {
    renderWithProvider(<DlpPage />);

    const agent = await screen.findByText('Contoso HR Agent');

    // Collapsed by default: the per-agent policies must not be on screen until asked for, so the
    // table stays readable when a tenant has many agents.
    expect(screen.queryByText('Payment card data')).not.toBeInTheDocument();

    const expander = agent.closest('button');
    expect(expander).not.toBeNull();
    expect(expander).toHaveAttribute('aria-expanded', 'false');

    fireEvent.click(expander!);

    expect(await screen.findByText('Policies affecting Contoso HR Agent')).toBeInTheDocument();
    expect(screen.getByText('Payment card data')).toBeInTheDocument();
    expect(expander).toHaveAttribute('aria-expanded', 'true');

    // Collapses again, so the control is a real toggle rather than one-way.
    fireEvent.click(expander!);
    expect(screen.queryByText('Payment card data')).not.toBeInTheDocument();
  });

  it('reveals which policies affected a given person when that person is selected', async () => {
    renderWithProvider(<DlpPage />);

    const person = await screen.findByText('ada@contoso.com');
    expect(screen.queryByText('HR records policy')).not.toBeInTheDocument();

    const expander = person.closest('button');
    expect(expander).not.toBeNull();
    fireEvent.click(expander!);

    expect(await screen.findByText('Policies affecting ada@contoso.com')).toBeInTheDocument();
    expect(screen.getByText('HR records policy')).toBeInTheDocument();

    // Expanding a person must not expand the agent alongside it - each row owns its own state.
    expect(screen.queryByText('Policies affecting Contoso HR Agent')).not.toBeInTheDocument();
  });

  it('does not offer an expander for an agent with no policy breakdown', async () => {
    mockSummary.mockResolvedValue(
      summary({
        topAgents: [
          { id: 'agent-1', name: 'Contoso HR Agent', blockedCount: 1, auditedCount: 0, usersAffected: 1, totalCount: 1, policies: [] },
        ],
      }),
    );

    renderWithProvider(<DlpPage />);

    const agent = await screen.findByText('Contoso HR Agent');
    expect(agent.closest('button')).toBeNull();
  });
});

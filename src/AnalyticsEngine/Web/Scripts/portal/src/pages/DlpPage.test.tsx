import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
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
    { id: 'agent-1', name: 'Contoso HR Agent', blockedCount: 8, auditedCount: 1, usersAffected: 4, totalCount: 9 },
  ],
  topUsers: [
    { id: '1', name: 'ada@contoso.com', blockedCount: 4, auditedCount: 0, usersAffected: null, totalCount: 4 },
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
});

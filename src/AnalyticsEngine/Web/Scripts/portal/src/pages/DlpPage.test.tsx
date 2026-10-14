import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, fireEvent, waitFor } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import DlpPage from './DlpPage';
import { GlobalFilterProvider } from '../components/globalFilter/GlobalFilterProvider';
import { formatNumber, loadCatalog, translateStatic } from '../i18n';
import type { DlpAvailability, DlpGovernanceSummary, DlpSummary } from '../types/dlp';

const mockAvailability = vi.fn();
const mockSummary = vi.fn();
const mockGovernance = vi.fn();

vi.mock('../api/dlpApi', () => ({
  fetchDlpAvailability: (...args: unknown[]) => mockAvailability(...args),
  fetchDlpSummary: (...args: unknown[]) => mockSummary(...args),
  fetchDlpGovernance: (...args: unknown[]) => mockGovernance(...args),
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

/**
 * 20,000 interactions; the jailbreak flag reported on 12,000 of them and raised on 3; the XPIA flag reported on
 * none - so its rate is unknown, which must never be shown as zero.
 */
const governance = (over: Partial<DlpGovernanceSummary> = {}): DlpGovernanceSummary => ({
  fromUtc: '2026-08-12T00:00:00Z',
  toUtc: '2026-09-09T00:00:00Z',
  interactions: 20000,
  jailbreak: { flaggedInteractions: 3, reportedInteractions: 12000, ratePer10000: 2.5 },
  xpia: { flaggedInteractions: 0, reportedInteractions: 0, ratePer10000: null },
  sensitivityLabels: { labelledResources: 25, resources: 100, interactionsWithResources: 60, share: 0.25 },
  interactionsWithModel: 900,
  models: [{ name: 'DEEP_LEO', interactions: 600, share: 0.03 }],
  interactionsWithPlugin: 2800,
  plugins: [{ name: 'BingWebSearch', interactions: 2800, share: 0.14 }],
  ...over,
});

beforeEach(() => {
  vi.clearAllMocks();
  mockAvailability.mockResolvedValue(availability());
  mockSummary.mockResolvedValue(summary());
  mockGovernance.mockResolvedValue(governance());
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

  it('hides the per-person table without See PII', async () => {
    renderWithProvider(<DlpPage />, { access: { administration: false, seePii: false } });

    expect(await screen.findByText('Contoso HR Agent')).toBeInTheDocument();
    expect(screen.queryByText('ada@contoso.com')).not.toBeInTheDocument();
    expect(screen.getByText('Individual details are hidden')).toBeInTheDocument();
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

  it('stops calling the DLP.All section tenant-wide while an administrator’s filter narrows it', async () => {
    const { unmount } = renderWithProvider(<DlpPage />);
    expect(await screen.findByText('Tenant-wide DLP activity')).toBeInTheDocument();
    expect(screen.getByText('Policies (tenant-wide)')).toBeInTheDocument();
    unmount();

    renderWithProvider(
      <GlobalFilterProvider
        value={{
          effective: { active: true, applied: true, bypassed: false, canBypass: false, revision: 1, filter: null, invalid: false },
        }}
      >
        <DlpPage />
      </GlobalFilterProvider>,
    );
    expect(await screen.findByText('DLP activity across all workloads')).toBeInTheDocument();
    expect(screen.getByText('Policies (all workloads)')).toBeInTheDocument();
    expect(screen.queryByText('Tenant-wide DLP activity')).not.toBeInTheDocument();
    expect(screen.queryByText('Policies (tenant-wide)')).not.toBeInTheDocument();
  });

  describe('governance signals (#648)', () => {
    it('shows each rate with its numerator, its denominator and how much of the period reported it', async () => {
      renderWithProvider(<DlpPage />);

      expect(await screen.findByText('Copilot governance signals')).toBeInTheDocument();
      expect(mockGovernance).toHaveBeenCalledWith(28);

      expect(screen.getByText('Jailbreak attempts')).toBeInTheDocument();
      expect(screen.getByText('2.5 per 10,000')).toBeInTheDocument();
      expect(screen.getByText('3 of the 12,000 interactions where Microsoft reported the signal')).toBeInTheDocument();
      expect(screen.getByText('Reported on 12,000 of 20,000 interactions in this period (60%).')).toBeInTheDocument();
    });

    it('calls a rate with no reported flag unknown, never zero', async () => {
      renderWithProvider(<DlpPage />);

      expect(await screen.findByText('Cross-prompt injection (XPIA)')).toBeInTheDocument();
      expect(screen.getByText('Not reported')).toBeInTheDocument();
      expect(
        screen.getByText('Microsoft reported this signal on none of the 20,000 interactions in this period, so there is no rate.'),
      ).toBeInTheDocument();
      expect(screen.queryByText('0 per 10,000')).not.toBeInTheDocument();
    });

    it('shows the share of labelled content with its denominator, and the model and plugin mix as Microsoft named it', async () => {
      renderWithProvider(<DlpPage />);

      expect(await screen.findByText('Labelled content used')).toBeInTheDocument();
      expect(screen.getByText('25%')).toBeInTheDocument();
      expect(screen.getByText('25 of the 100 resources Copilot used carried a sensitivity label')).toBeInTheDocument();

      expect(screen.getByText('DEEP_LEO')).toBeInTheDocument();
      expect(screen.getByText('3%')).toBeInTheDocument();
      expect(screen.getByText('BingWebSearch')).toBeInTheDocument();
      expect(screen.getByText('14%')).toBeInTheDocument();
      expect(screen.getByText(/A model was named on 900 of them/)).toBeInTheDocument();
    });

    it('lists the agents DLP blocked and points to their verdicts in Copilot Adoption', async () => {
      renderWithProvider(<DlpPage />);

      expect(await screen.findByText('Agents with DLP blocks')).toBeInTheDocument();
      expect(screen.getByText(/Contoso HR Agent · Blocked: 8/)).toBeInTheDocument();

      const link = screen.getByRole('link', { name: 'Copilot Adoption > Agents' });
      expect(link).toHaveAttribute('href', '#/insights/copilot-adoption');
    });

    it('keeps the DLP figures when the governance call fails, and says so in its own section', async () => {
      mockGovernance.mockRejectedValue(new Error('governance boom'));
      renderWithProvider(<DlpPage />);

      expect(await screen.findByText('governance boom')).toBeInTheDocument();
      expect(screen.getByText('Who and what is affected')).toBeInTheDocument();
      expect(screen.getAllByText('Contoso HR Agent').length).toBeGreaterThan(0);
    });

    it('does not ask for the signals when the Copilot import is off', async () => {
      mockAvailability.mockResolvedValue(availability({ copilotDlpAvailable: false }));
      renderWithProvider(<DlpPage />);

      expect(
        await screen.findByText('The Copilot audit import is switched off, so there are no governance signals to show.'),
      ).toBeInTheDocument();
      expect(mockGovernance).not.toHaveBeenCalled();
    });

    it('asks again for the new period when the reader changes it', async () => {
      renderWithProvider(<DlpPage />);
      await screen.findByText('2.5 per 10,000');

      fireEvent.change(screen.getByLabelText('Reporting period'), { target: { value: '90' } });

      await waitFor(() => expect(mockGovernance).toHaveBeenLastCalledWith(90));
    });

    it('renders the whole section in Spanish, with Spanish numbers and Microsoft’s names untouched', async () => {
      await loadCatalog('es');
      const es = (key: Parameters<typeof translateStatic>[1], values?: Record<string, string>) =>
        translateStatic('es', key, values);

      renderWithProvider(<DlpPage />, { language: 'es' });

      expect(await screen.findByText(es('dlp.governance.title'))).toBeInTheDocument();
      expect(screen.getByText(es('dlp.governance.jailbreak.title'))).toBeInTheDocument();
      expect(screen.getByText(es('dlp.governance.xpia.title'))).toBeInTheDocument();
      expect(screen.getByText(es('dlp.governance.rate.notReported'))).toBeInTheDocument();

      // Formatted in the page's language: 2,5 and 12.000 in Spanish, not 2.5 and 12,000.
      expect(screen.getByText(es('dlp.governance.rate.value', { rate: formatNumber(2.5, { maximumFractionDigits: 1 }) }))).toBeInTheDocument();
      expect(
        screen.getByText(es('dlp.governance.rate.fraction', { flagged: formatNumber(3), reported: formatNumber(12000) })),
      ).toBeInTheDocument();
      expect(screen.getByText('2,5 por cada 10.000')).toBeInTheDocument();

      expect(screen.getByRole('link', { name: es('dlp.governance.agents.pointerLink') })).toHaveAttribute(
        'href',
        '#/insights/copilot-adoption',
      );

      // Data Microsoft named is never translated.
      expect(screen.getByText('DEEP_LEO')).toBeInTheDocument();
      expect(screen.getByText('BingWebSearch')).toBeInTheDocument();

      for (const english of ['Copilot governance signals', 'Jailbreak attempts', 'Not reported', 'per 10,000', 'Labelled content used']) {
        expect(document.body.textContent).not.toContain(english);
      }
    });
  });
});

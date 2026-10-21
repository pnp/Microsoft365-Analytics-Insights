import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { renderWithProvider } from '../../../test/renderWithProvider';
import type { LiveLicenceComparison, ReportLicenceVerdict, UserDiagnostics, UserRefreshRequest } from '../../../types/userDiagnostics';
import UserDiagnosticsPanel from './UserDiagnosticsPanel';
import TroubleshootUserLink, { USER_LOOKUP_PATH } from '../TroubleshootUserLink';
import { fetchLiveLicences, fetchReportVerdict, fetchUserRefresh, requestUserRefresh } from '../../../api/userLookupApi';
import { REFRESH_POLL_MS } from './UserRefreshCard';

vi.mock('../../../api/userLookupApi', () => ({
  fetchLiveLicences: vi.fn(),
  fetchReportVerdict: vi.fn(),
  fetchUserCoverage: vi.fn(),
  fetchUserRefresh: vi.fn(),
  requestUserRefresh: vi.fn(),
}));

const UPN = 'jane.doe@contoso.example';

function diagnostics(overrides: Partial<UserDiagnostics> = {}): UserDiagnostics {
  return {
    lookedUpUpn: UPN,
    storedUser: {
      userId: 42,
      userPrincipalName: UPN,
      mail: null,
      azureAdId: '00000000-0000-0000-0000-000000000001',
      accountEnabled: true,
      metadataLastWrittenUtc: '2026-09-01T10:00:00Z',
    },
    scope: { decision: 'unfiltered', liveComparisonAllowed: true, resolvedUtc: null, matchedGroupCount: null, memberCount: null, failureKind: null },
    licences: [
      {
        licenceTypeId: 2,
        name: 'Contoso Sales Copilot',
        skuPartNumber: 'Microsoft_Copilot_for_Sales',
        classification: { isCopilotSeat: false, reason: 'excludedSkuPrefix', matchedRule: 'MICROSOFT_COPILOT_FOR_SALES' },
        catalogueListed: true,
        catalogueCarriesCopilotSeat: true,
        heldSinceUtc: '2026-08-01T00:00:00Z',
        heldSinceKind: 'beforeTrackingBegan',
      },
    ],
    history: { available: true, trackingBeganUtc: '2026-08-01T00:00:00Z', rows: [], truncated: false },
    freshness: {
      userImportEnabled: true,
      userImportIntervalHours: 24,
      userImportLastCompletedUtc: '2026-10-20T06:00:00Z',
      userImportStampState: 'recorded',
      lastTenantLicenceRefreshUtc: '2026-10-20T06:00:00Z',
      userVerification: 'notRecorded',
      userVerifiedUtc: null,
    },
    catalogue: {
      ruleSetVersion: '2026-10-e7',
      buildLabel: 'test',
      catalogueContentHash: 'abcdef012345',
      catalogueSkuCount: 600,
      seatSkuPrefixes: ['M365_COPILOT'],
      excludedSkuPrefixes: ['MICROSOFT_COPILOT_FOR_SALES'],
      copilotSeatServicePlans: ['M365_COPILOT_APPS', 'M365_COPILOT_BUSINESS_CHAT'],
      tenantLicenceTypes: [
        {
          id: 2,
          name: 'Contoso Sales Copilot',
          skuPartNumber: 'Microsoft_Copilot_for_Sales',
          assignedUsers: 3,
          classification: { isCopilotSeat: false, reason: 'excludedSkuPrefix', matchedRule: 'MICROSOFT_COPILOT_FOR_SALES' },
          catalogueListed: true,
          catalogueCarriesCopilotSeat: true,
          possibleRuleGap: true,
        },
      ],
    },
    findings: [
      { key: 'noStoredCopilotSeat', severity: 'info' },
      { key: 'possibleCopilotRuleGap', severity: 'warning', values: { name: 'Contoso Sales Copilot', sku: 'Microsoft_Copilot_for_Sales', counted: false } },
      { key: 'aFindingFromANewerServer', severity: 'warning' },
    ],
    loadedAtUtc: '2026-10-21T12:00:00Z',
    ...overrides,
  };
}

beforeEach(() => {
  vi.mocked(fetchLiveLicences).mockReset();
  vi.mocked(fetchReportVerdict).mockReset();
  vi.mocked(fetchUserRefresh).mockReset();
  vi.mocked(requestUserRefresh).mockReset();
});

function refreshRequest(overrides: Partial<UserRefreshRequest> = {}): UserRefreshRequest {
  return {
    id: 'r1',
    userPrincipalName: UPN,
    userId: 42,
    requestedBy: 'admin@contoso.example',
    requestedUtc: '2026-10-21T12:00:00Z',
    expiresUtc: '2026-10-22T12:00:00Z',
    state: 'queued',
    attempts: 0,
    startedUtc: null,
    completedUtc: null,
    observedUtc: null,
    outcome: null,
    added: [],
    removed: [],
    heldBack: [],
    profileChanges: [],
    notes: [],
    ...overrides,
  };
}

describe('UserRefreshCard', () => {
  it('asks for confirmation, queues the refresh, follows it and reports what changed', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      vi.mocked(requestUserRefresh).mockResolvedValue({ accepted: true, rejection: null, request: refreshRequest() });
      vi.mocked(fetchUserRefresh).mockResolvedValue(
        refreshRequest({
          state: 'completed',
          outcome: 'partial',
          observedUtc: '2026-10-21T12:05:00Z',
          added: ['Microsoft 365 Copilot'],
          heldBack: ['Office 365 E3 (old name)'],
          profileChanges: ['department', 'aFieldFromANewerServer'],
          notes: ['ambiguousLicenceKept'],
        }),
      );
      const onDataChanged = vi.fn();
      renderWithProvider(
        <UserDiagnosticsPanel
          diagnostics={diagnostics({ refresh: { available: true, reason: null, active: null, recent: [] } })}
          onDataChanged={onDataChanged}
        />,
      );

      fireEvent.click(screen.getByRole('button', { name: 'Refresh from the directory...' }));
      expect(requestUserRefresh).not.toHaveBeenCalled();
      expect(screen.getByText(/Re-read jane.doe@contoso.example from Microsoft Graph/)).toBeInTheDocument();

      fireEvent.click(screen.getByRole('button', { name: 'Refresh this person' }));
      expect(await screen.findByText(/Waiting for the importer since/)).toBeInTheDocument();
      expect(requestUserRefresh).toHaveBeenCalledWith(UPN);

      await vi.advanceTimersByTimeAsync(REFRESH_POLL_MS);

      expect(await screen.findByText(/Partly done/)).toBeInTheDocument();
      expect(screen.getByText('Microsoft 365 Copilot')).toBeInTheDocument();
      expect(screen.getByText('Office 365 E3 (old name)')).toBeInTheDocument();
      expect(screen.getByText('Department')).toBeInTheDocument();
      expect(screen.queryByText(/aFieldFromANewerServer/)).not.toBeInTheDocument();
      expect(screen.getByText(/this person still holds that SKU/)).toBeInTheDocument();
      expect(onDataChanged).toHaveBeenCalledTimes(1);
      expect(screen.getByRole('button', { name: 'Refresh from the directory...' })).toBeEnabled();
    } finally {
      vi.useRealTimers();
    }
  });

  it('explains why a refresh is not offered, and sends nothing', () => {
    renderWithProvider(
      <UserDiagnosticsPanel diagnostics={diagnostics({ refresh: { available: false, reason: 'scopeUnknown', active: null, recent: [] } })} />,
    );

    expect(screen.getByText('Not available until the user scope can be resolved.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Refresh from the directory...' })).not.toBeInTheDocument();
    expect(requestUserRefresh).not.toHaveBeenCalled();
  });

  it('reads in Spanish, including the recent requests', async () => {
    const done = refreshRequest({ id: 'r0', state: 'completed', outcome: 'unchanged' });
    renderWithProvider(
      <UserDiagnosticsPanel diagnostics={diagnostics({ refresh: { available: true, reason: null, active: null, recent: [done] } })} />,
      { language: 'es' },
    );

    expect(await screen.findByText('Actualizar a esta persona desde el directorio')).toBeInTheDocument();
    expect(screen.getByText('Ya era correcta')).toBeInTheDocument();
    expect(screen.getByText('admin@contoso.example')).toBeInTheDocument();
  });
});

describe('UserDiagnosticsPanel', () => {
  it('explains why a licence is not a Copilot seat, and flags the catalogue disagreeing with the rules', () => {
    renderWithProvider(<UserDiagnosticsPanel diagnostics={diagnostics()} />);

    expect(screen.getByText(/catalogue lists the Microsoft 365 Copilot seat service plans in Contoso Sales Copilot \(Microsoft_Copilot_for_Sales\)/)).toBeInTheDocument();
    expect(screen.getAllByText(/a Copilot-branded product that is not a Microsoft 365 Copilot seat/).length).toBeGreaterThan(0);
    expect(screen.getByText('Rules and catalogue disagree')).toBeInTheDocument();
    // A whole-tenant run is context, never "this person was checked".
    expect(screen.getByText('Last confirmed for this person').nextSibling?.textContent).toBe('Not recorded');
    // A finding key this build does not know is left out rather than shown raw.
    expect(screen.queryByText('aFindingFromANewerServer')).not.toBeInTheDocument();
  });

  it('reads in Spanish', async () => {
    renderWithProvider(<UserDiagnosticsPanel diagnostics={diagnostics()} />, { language: 'es' });

    expect(await screen.findByText('Diagnóstico de licencias y datos')).toBeInTheDocument();
    expect(screen.getByText('Las reglas y el catálogo no coinciden')).toBeInTheDocument();
  });

  it('offers no live directory read for someone outside the import scope', () => {
    renderWithProvider(
      <UserDiagnosticsPanel
        diagnostics={diagnostics({
          scope: { decision: 'excluded', liveComparisonAllowed: false, resolvedUtc: '2026-10-21T00:00:00Z', matchedGroupCount: 1, memberCount: 10, failureKind: null },
        })}
      />,
    );

    expect(screen.getByRole('button', { name: 'Compare with Microsoft Graph' })).toBeDisabled();
    expect(screen.getByText('Not available: this person is outside the configured user scope.')).toBeInTheDocument();
    expect(fetchLiveLicences).not.toHaveBeenCalled();
  });

  it('shows a licence the directory assigns but the database does not hold', async () => {
    const comparison: LiveLicenceComparison = {
      status: 'ok',
      reason: null,
      httpStatus: null,
      observedAtUtc: '2026-10-21T12:00:00Z',
      directory: { objectId: '00000000-0000-0000-0000-000000000001', userPrincipalName: UPN, accountEnabled: true, objectIdMatchesStored: true, userPrincipalNameDiffersFromStored: false },
      complete: true,
      partNumberSource: 'licenseDetails',
      rows: [
        {
          licenceTypeId: 1,
          name: 'Microsoft 365 Copilot',
          skuPartNumber: 'Microsoft_365_Copilot',
          stored: false,
          live: true,
          liveSkus: [
            { skuId: '00000000-0000-0000-0000-0000000000c1', skuPartNumber: 'Microsoft_365_Copilot', catalogueName: 'Microsoft 365 Copilot', assignmentPaths: ['group'], state: 'Active', error: null, copilotSeatPlans: 'disabled' },
          ],
          mapping: 'unique',
          comparison: 'liveOnly',
          classification: { isCopilotSeat: true, reason: 'seatSkuPrefix', matchedRule: 'MICROSOFT_365_COPILOT' },
        },
      ],
    };
    vi.mocked(fetchLiveLicences).mockResolvedValue(comparison);
    renderWithProvider(<UserDiagnosticsPanel diagnostics={diagnostics()} />);

    fireEvent.click(screen.getByRole('button', { name: 'Compare with Microsoft Graph' }));

    expect(await screen.findByText('Only in directory')).toBeInTheDocument();
    expect(screen.getByText('Assigned through a group')).toBeInTheDocument();
    expect(screen.getByText('Copilot seat plans switched off for this person')).toBeInTheDocument();
    expect(fetchLiveLicences).toHaveBeenCalledWith(UPN, expect.any(AbortSignal));
  });
});

describe('ReportVerdictCard', () => {
  it('answers the question the report asked as soon as it opens, with that report\'s period and seats', async () => {
    const verdict: ReportLicenceVerdict = {
      report: 'copilotAdoption',
      verdict: 'unlicensed',
      basis: 'currentAssignments',
      fromUtc: '2026-09-23T12:00:00Z',
      toExclusiveUtc: '2026-10-21T12:00:00Z',
      windowDays: 28,
      usesExplicitDates: false,
      historyTrackingBeganUtc: null,
      seatSelection: 'reportOverride',
      countedLicenceTypes: [],
      decidingRows: [],
      notes: ['rollingWindowUsesCurrentAssignments'],
      isRecomputation: true,
      recomputedAtUtc: '2026-10-21T12:00:00Z',
      sql: 'SELECT 1;',
    };
    vi.mocked(fetchReportVerdict).mockResolvedValue(verdict);

    renderWithProvider(
      <UserDiagnosticsPanel diagnostics={diagnostics()} context={{ upn: UPN, report: 'copilotAdoption', windowDays: 90, licenceTypeIds: [7] }} />,
    );

    expect(await screen.findByText('Unlicensed')).toBeInTheDocument();
    expect(screen.getByText(/A rolling period uses the licences stored now/)).toBeInTheDocument();
    expect(fetchReportVerdict).toHaveBeenCalledWith(
      UPN,
      expect.objectContaining({ report: 'copilotAdoption', windowDays: 90, licenceTypeIds: [7] }),
      expect.any(AbortSignal),
    );
  });
});

describe('TroubleshootUserLink', () => {
  function Landing() {
    const location = useLocation();
    return <div data-testid="landed">{JSON.stringify({ path: location.pathname, search: location.search, state: location.state })}</div>;
  }

  it('opens User lookup with the report context in router state, never in the URL', () => {
    renderWithProvider(
      <MemoryRouter initialEntries={['/copilot']}>
        <Routes>
          <Route path="/copilot" element={<TroubleshootUserLink context={{ upn: UPN, report: 'copilotAdoption', windowDays: 28 }} />} />
          <Route path={USER_LOOKUP_PATH} element={<Landing />} />
        </Routes>
      </MemoryRouter>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Troubleshoot this person' }));

    const landed = JSON.parse(screen.getByTestId('landed').textContent ?? '{}');
    expect(landed.path).toBe(USER_LOOKUP_PATH);
    expect(landed.search).toBe('');
    expect(landed.state.diagnostics.upn).toBe(UPN);
  });

  it('is not offered to a reader who could not open User lookup', async () => {
    renderWithProvider(
      <MemoryRouter>
        <TroubleshootUserLink context={{ upn: UPN, report: 'copilotAdoption' }} />
      </MemoryRouter>,
      { access: { status: 'ready', enforced: true, administration: true, seePii: false } },
    );

    await waitFor(() => expect(screen.queryByRole('button', { name: 'Troubleshoot this person' })).not.toBeInTheDocument());
  });
});

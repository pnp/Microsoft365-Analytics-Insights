import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  makeStyles,
  tokens,
  Title3,
  Body1,
  Text,
  Card,
  Select,
  Button,
  Tooltip,
  MessageBar,
  MessageBarBody,
} from '@fluentui/react-components';
import { ArrowDownload16Regular } from '@fluentui/react-icons';
import { fetchAvailability, fetchOverview, downloadExport } from '../api/licenceActivityApi';
import type {
  DateRange,
  LicenceActivityAvailability,
  LicenceActivityOverview,
} from '../types/licenceActivity';
import Spinner from '../components/Spinner';
import DateRangeControl from '../components/licenceActivity/DateRangeControl';
import CoveragePanel from '../components/licenceActivity/CoveragePanel';
import SkuAssignments from '../components/licenceActivity/SkuAssignments';
import WorkloadDistributions from '../components/licenceActivity/WorkloadDistributions';
import DemographicBreakdown from '../components/licenceActivity/DemographicBreakdown';
import UsersDrillDown from '../components/licenceActivity/UsersDrillDown';
import ApiErrorBar, { describeError } from '../components/licenceActivity/ApiErrorBar';
import { presetRange } from '../components/licenceActivity/dateRange';
import { formatCount, licenceName } from '../components/licenceActivity/format';
import {
  mergeDemographicOptions,
  EMPTY_CATALOGUE,
  DEMOGRAPHIC_OPTION_CAP,
  type DemographicCatalogue,
} from '../components/licenceActivity/demographicOptions';

const useStyles = makeStyles({
  header: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
  },
  intro: {
    marginTop: '8px',
    maxWidth: '820px',
  },
  controlsCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    marginTop: '16px',
    padding: '14px 16px',
  },
  controlRow: {
    display: 'flex',
    alignItems: 'flex-end',
    gap: '16px',
    flexWrap: 'wrap',
  },
  field: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  fieldLabel: {
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
    color: tokens.colorNeutralForeground3,
  },
  exportWrap: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-end',
    gap: '4px',
  },
  stack: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    marginTop: '16px',
  },
  sectionHead: {
    display: 'flex',
    alignItems: 'baseline',
    gap: '10px',
    marginTop: '8px',
    paddingBottom: '6px',
    borderBottomWidth: '2px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorBrandStroke1,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  center: {
    textAlign: 'center',
    padding: '32px',
  },
});

/**
 * Licence activity report (issues #436 / #437).
 *
 * Answers, for an IT / licensing admin: which licences are assigned, and how much are the people who
 * hold them actually using each Microsoft 365 workload? It is deliberately an ACTIVITY report - no
 * blended "productivity" score, no "remove this licence" button; it surfaces the evidence and leaves
 * the decision with the admin.
 *
 * Everyone signed in sees the aggregates. Drilling into named individuals additionally requires the
 * opt-in `LicenceActivity.ReadUsers` Entra role; the UI hides the drill-down without it, and the
 * server enforces it regardless.
 */
export default function LicenceActivityPage() {
  const styles = useStyles();

  const [availability, setAvailability] = useState<LicenceActivityAvailability | null>(null);
  const [availabilityError, setAvailabilityError] = useState<unknown>(null);
  const [availabilityLoading, setAvailabilityLoading] = useState(true);
  // Bumped whenever the viewer's ROLE may have changed under them, so availability (and therefore
  // canViewUsers) is re-read instead of staying frozen at page load for the life of the tab.
  const [availabilityKey, setAvailabilityKey] = useState(0);

  // The reporting window lives here, so it is preserved across demographic-filter and licence
  // changes - only the date control (or a preset click) ever changes it. Defaults to 28 days.
  const [range, setRange] = useState<DateRange>(() => presetRange(28));
  const [departmentId, setDepartmentId] = useState<number | null>(null);
  const [countryId, setCountryId] = useState<number | null>(null);

  const [overviewResult, setOverviewResult] = useState<{
    key: string;
    data: LicenceActivityOverview | null;
    error: unknown;
  } | null>(null);
  const [overviewReloadKey, setOverviewReloadKey] = useState(0);

  // Filter options are kept across overview reloads AND merged (never replaced) so the drop-downs
  // don't collapse to just the selected group. The backend only returns demographic groups WITHIN the
  // current scope (selecting department X returns just X), so replacing would strand the user unable to
  // switch straight to Y. We keep a union of every id/name seen; the scope-specific COUNTS live in the
  // demographic breakdown, not here, so these options carry names only and never mislabel a count.
  const [departmentOptions, setDepartmentOptions] = useState<DemographicCatalogue>(EMPTY_CATALOGUE);
  const [countryOptions, setCountryOptions] = useState<DemographicCatalogue>(EMPTY_CATALOGUE);

  const [selectedLicenceTypeId, setSelectedLicenceTypeId] = useState<number | null>(null);
  const [usersId, setUsersId] = useState<string | null>(null);
  // Bumped to force the drill-down to re-mint its users snapshot after an expiry, without changing the
  // params (which stay put so the admin's licence/workload/page/filters are preserved).
  const [usersRefreshToken, setUsersRefreshToken] = useState(0);

  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState<unknown>(null);

  const overviewSeqRef = useRef(0);

  const canViewUsers = availability?.canViewUsers === true;

  // --- Availability -------------------------------------------------------------------------------
  useEffect(() => {
    let cancelled = false;
    const controller = new AbortController();
    setAvailabilityLoading(true);
    setAvailabilityError(null);
    fetchAvailability(controller.signal)
      .then((a) => {
        if (!cancelled) setAvailability(a);
      })
      .catch((e) => {
        if (cancelled || controller.signal.aborted) return;
        setAvailabilityError(e);
      })
      .finally(() => {
        if (!cancelled) setAvailabilityLoading(false);
      });
    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [availabilityKey]);

  // The scope key identifying the overview request. When it changes (date / department / country) the
  // previous overview and its snapshot id must vanish on the SAME render, so nothing stale can be shown
  // or exported against a scope the user has already moved on from.
  const overviewKey = availability?.available
    ? JSON.stringify({ from: range.from, to: range.to, departmentId, countryId })
    : null;

  // --- Overview (cancellable, stale-safe, request-scope bound) ------------------------------------
  useEffect(() => {
    // Bump on every run so a late/aborted response from a prior scope is dropped.
    const mySeq = (overviewSeqRef.current += 1);
    if (overviewKey === null) return;

    const controller = new AbortController();
    fetchOverview({ from: range.from, to: range.to, departmentId, countryId }, controller.signal)
      .then((o) => {
        if (mySeq === overviewSeqRef.current) setOverviewResult({ key: overviewKey, data: o, error: null });
      })
      .catch((err) => {
        if (mySeq !== overviewSeqRef.current || controller.signal.aborted) return;
        if (err instanceof DOMException && err.name === 'AbortError') return;
        setOverviewResult({ key: overviewKey, data: null, error: err });
      });

    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [overviewKey, overviewReloadKey]);

  // Only surface an overview that belongs to the CURRENT scope key. A filter change hides the previous
  // overview (and its export id) on this render, before the new request starts.
  const overviewBelongs = overviewResult !== null && overviewResult.key === overviewKey;
  const overview = overviewBelongs ? overviewResult!.data : null;
  const overviewError = overviewBelongs ? overviewResult!.error : null;
  const overviewLoading = overviewKey !== null && !overviewBelongs;

  // Refresh persisted filter options and choose/validate the selected licence against a new overview.
  // `overview` is request-scope bound, so when it is present the page's departmentId/countryId are the
  // scope that produced it - which is exactly what tells each dimension whether this reply is its
  // authoritative unfiltered catalogue, and which id to pin so the user can switch away from it.
  useEffect(() => {
    if (!overview) return;
    setDepartmentOptions((prev) =>
      mergeDemographicOptions(prev, overview.departments, {
        unfilteredForDimension: departmentId == null,
        selectedId: departmentId,
      }),
    );
    setCountryOptions((prev) =>
      mergeDemographicOptions(prev, overview.countries, {
        unfilteredForDimension: countryId == null,
        selectedId: countryId,
      }),
    );
    setSelectedLicenceTypeId((prev) => {
      if (prev != null && overview.licences.some((s) => s.licenceTypeId === prev)) return prev;
      // Default to the most-assigned licence so the distributions show something immediately.
      const biggest = [...overview.licences].sort((a, b) => b.assignedUsers - a.assignedUsers)[0];
      return biggest ? biggest.licenceTypeId : null;
    });
  }, [overview, departmentId, countryId]);

  // A new overview snapshot invalidates any users snapshot captured for the export.
  useEffect(() => {
    setUsersId(null);
    setExportError(null);
  }, [overview?.snapshotId]);

  const reloadOverview = useCallback(() => setOverviewReloadKey((k) => k + 1), []);
  const handleUsersSnapshot = useCallback((id: string | null) => setUsersId(id), []);

  // The correct response to an EXPIRED snapshot (export 410, or a users 410): re-mint the snapshots in
  // place. We drop the stale users id and error, force the drill-down to re-fetch a fresh users
  // snapshot (its params are unchanged, so the licence/workload/page/filters are preserved), and reload
  // the overview. The overview often comes back under the SAME cached id, which is exactly why clearing
  // is explicit here rather than relying on the snapshot-id-changed effect. It deliberately does NOT
  // re-export: an export must be an explicit action, never a silent re-query of freshly minted data.
  const refreshSnapshots = useCallback(() => {
    setExportError(null);
    setUsersId(null);
    setUsersRefreshToken((t) => t + 1);
    setOverviewReloadKey((k) => k + 1);
    // Re-read availability too. The feature contract for the 410/409 recovery path is "re-mint AND
    // recheck role": a role granted or revoked mid-session must not stay invisible until the admin
    // reloads the whole page. (The server enforces the role independently on every /users and every
    // /export carrying a usersId, so this is a dead-end fix, not a security fix.)
    setAvailabilityKey((k) => k + 1);
  }, []);

  const selectedLicence = useMemo(
    () => overview?.licences.find((s) => s.licenceTypeId === selectedLicenceTypeId) ?? null,
    [overview, selectedLicenceTypeId],
  );

  // Only attach a users snapshot to the export when the viewer is allowed per-user detail and is
  // actually looking at a licence's list; otherwise the workbook is aggregate-only.
  const exportUsersId = canViewUsers && selectedLicence ? usersId ?? undefined : undefined;

  const onExport = async (): Promise<void> => {
    if (!overview) return;
    setExporting(true);
    setExportError(null);
    try {
      await downloadExport({ overviewId: overview.snapshotId, usersId: exportUsersId });
    } catch (err) {
      setExportError(err);
      // A 403 here means the LicenceActivity.ReadUsers role went away mid-session while the export
      // was still attaching a usersId. Retrying unchanged would 403 forever, so drop the individual
      // snapshot and re-read availability: the next export is then a valid aggregate-only workbook.
      if (describeError(err, '').kind === 'forbidden') {
        setUsersId(null);
        setAvailabilityKey((k) => k + 1);
      }
    } finally {
      setExporting(false);
    }
  };

  const exportExpired = describeError(exportError, '').kind === 'expired';

  return (
    <div>
      <div className={styles.header}>
        <div>
          <Title3>Licence activity</Title3>
          <Body1 block className={styles.intro}>
            Which licences are assigned, and how much are the people who hold them actually using each Microsoft 365
            workload. Activity is shown per workload and never blended into a single score, and anything that was not
            imported is shown as &quot;Unknown&quot; rather than zero.
          </Body1>
        </div>
      </div>

      {availabilityLoading && (
        <div className={styles.center}>
          <Spinner size={80} label="Checking availability..." />
        </div>
      )}

      {availabilityError != null && (
        <ApiErrorBar
          error={availabilityError}
          fallback="Failed to check licence activity availability."
          onRetry={() => window.location.reload()}
          retryLabel="Reload"
        />
      )}

      {availability && !availability.available && (
        <MessageBar intent="info" style={{ marginTop: '16px' }}>
          <MessageBarBody>
            Licence activity reporting is not available on this deployment.
            {availability.messages.length > 0 && (
              <ul style={{ margin: '6px 0 0 0', paddingInlineStart: '20px' }}>
                {availability.messages.map((m) => (
                  <li key={m}>{m}</li>
                ))}
              </ul>
            )}
          </MessageBarBody>
        </MessageBar>
      )}

      {availability?.available && (
        <>
          {availability.messages.length > 0 && (
            <MessageBar intent="info" style={{ marginTop: '16px' }}>
              <MessageBarBody>
                <ul style={{ margin: 0, paddingInlineStart: '20px' }}>
                  {availability.messages.map((m) => (
                    <li key={m}>{m}</li>
                  ))}
                </ul>
              </MessageBarBody>
            </MessageBar>
          )}

          <Card className={styles.controlsCard}>
            <div className={styles.controlRow}>
              <div className={styles.field}>
                <Text size={200} className={styles.fieldLabel}>
                  Reporting window
                </Text>
                <DateRangeControl
                  value={range}
                  onChange={setRange}
                  minDays={availability.minimumDays}
                  maxDays={availability.maximumDays}
                />
              </div>

              <div className={styles.field}>
                <Text size={200} className={styles.fieldLabel}>
                  Department
                </Text>
                <Select
                  value={departmentId == null ? '' : String(departmentId)}
                  aria-label="Filter by department"
                  disabled={departmentOptions.options.length === 0}
                  onChange={(_e, d) => setDepartmentId(d.value === '' ? null : Number(d.value))}
                >
                  <option value="">All departments</option>
                  {departmentOptions.options.map((dept) => (
                    <option key={dept.id} value={dept.id}>
                      {dept.name}
                    </option>
                  ))}
                </Select>
                {departmentOptions.truncated && (
                  <Text size={100} className={styles.muted}>
                    Showing up to {formatCount(DEMOGRAPHIC_OPTION_CAP)} recent options; some may be hidden.
                  </Text>
                )}
              </div>

              <div className={styles.field}>
                <Text size={200} className={styles.fieldLabel}>
                  Country
                </Text>
                <Select
                  value={countryId == null ? '' : String(countryId)}
                  aria-label="Filter by country"
                  disabled={countryOptions.options.length === 0}
                  onChange={(_e, d) => setCountryId(d.value === '' ? null : Number(d.value))}
                >
                  <option value="">All countries</option>
                  {countryOptions.options.map((country) => (
                    <option key={country.id} value={country.id}>
                      {country.name}
                    </option>
                  ))}
                </Select>
                {countryOptions.truncated && (
                  <Text size={100} className={styles.muted}>
                    Showing up to {formatCount(DEMOGRAPHIC_OPTION_CAP)} recent options; some may be hidden.
                  </Text>
                )}
              </div>

              <div style={{ flexGrow: 1 }} />

              <div className={styles.exportWrap}>
                <Tooltip
                  relationship="description"
                  content={
                    overview
                      ? exportUsersId
                        ? 'Excel snapshot of the overview plus the exact user rows currently in view. Built from the cached snapshot, so it matches the screen rather than re-querying.'
                        : 'Excel snapshot of the licence and workload overview (aggregate only). Built from the cached snapshot.'
                      : 'Available once the overview has loaded.'
                  }
                >
                  <Button
                    appearance="primary"
                    icon={<ArrowDownload16Regular />}
                    disabled={!overview || exporting}
                    onClick={onExport}
                  >
                    {exporting ? 'Exporting...' : 'Export to Excel'}
                  </Button>
                </Tooltip>
              </div>
            </div>

            {exportError != null && (
              <ApiErrorBar
                error={exportError}
                fallback="Couldn't export the workbook."
                onRetry={exportExpired ? refreshSnapshots : onExport}
                retryLabel={exportExpired ? 'Refresh' : 'Try again'}
              />
            )}
          </Card>

          {overviewLoading && (
            <div className={styles.center}>
              <Spinner size={80} label="Loading licence activity..." />
            </div>
          )}

          {overviewError != null && (
            <div style={{ marginTop: '16px' }}>
              <ApiErrorBar
                error={overviewError}
                fallback="Failed to load the licence activity overview."
                onRetry={reloadOverview}
              />
            </div>
          )}

          {overview && (
            <div className={styles.stack}>
              <CoveragePanel
                generatedUtc={overview.generatedUtc}
                expiresUtc={overview.expiresUtc}
                coverage={overview.coverage}
              />

              {overview.messages.length > 0 && (
                <MessageBar intent="info">
                  <MessageBarBody>
                    <ul style={{ margin: 0, paddingInlineStart: '20px' }}>
                      {overview.messages.map((m) => (
                        <li key={m}>{m}</li>
                      ))}
                    </ul>
                  </MessageBarBody>
                </MessageBar>
              )}

              <Text size={200} className={styles.muted}>
                {formatCount(overview.distinctAssignedUsers)} distinct users hold a licence in this scope.
                {overview.demographicsTruncated &&
                  ' Department and country lists are capped and may not be exhaustive.'}
              </Text>

              <SkuAssignments
                licences={overview.licences}
                selectedLicenceTypeId={selectedLicenceTypeId}
                onSelect={setSelectedLicenceTypeId}
              />

              {selectedLicence && (
                <div>
                  <div className={styles.sectionHead}>
                    <Text weight="semibold" size={500}>
                      Workload activity
                    </Text>
                    <Text size={200} className={styles.muted}>
                      {licenceName(selectedLicence)} &middot; five workloads, measured separately
                    </Text>
                  </div>
                  <div style={{ marginTop: '12px' }}>
                    <WorkloadDistributions workloads={selectedLicence.workloads} />
                  </div>
                </div>
              )}

              {(overview.departments.length > 0 || overview.countries.length > 0) && (
                <div>
                  <div className={styles.sectionHead}>
                    <Text weight="semibold" size={500}>
                      Activity by demographic
                    </Text>
                    <Text size={200} className={styles.muted}>
                      Assigned licences and workload activity by department and country
                    </Text>
                  </div>
                  <div style={{ marginTop: '12px', display: 'flex', flexDirection: 'column', gap: '16px' }}>
                    <DemographicBreakdown
                      title="By department"
                      segmentLabel="Department"
                      rows={overview.departments}
                      truncated={overview.demographicsTruncated}
                    />
                    <DemographicBreakdown
                      title="By country"
                      segmentLabel="Country"
                      rows={overview.countries}
                      truncated={overview.demographicsTruncated}
                    />
                  </div>
                </div>
              )}

              {canViewUsers ? (
                <div>
                  <div className={styles.sectionHead}>
                    <Text weight="semibold" size={500}>
                      User drill-down
                    </Text>
                    <Text size={200} className={styles.muted}>
                      Who is and isn&apos;t using a licence
                    </Text>
                  </div>
                  <div style={{ marginTop: '12px' }}>
                    {selectedLicence ? (
                      <UsersDrillDown
                        key={selectedLicence.licenceTypeId}
                        overviewId={overview.snapshotId}
                        licence={selectedLicence}
                        coverage={overview.coverage}
                        onUsersSnapshot={handleUsersSnapshot}
                        onRefreshOverview={refreshSnapshots}
                        refreshToken={usersRefreshToken}
                      />
                    ) : (
                      <Card>
                        <Text className={styles.muted}>
                          Select a licence in the assignments table above to see its most and least active users, or
                          to browse everyone who holds it.
                        </Text>
                      </Card>
                    )}
                  </div>
                </div>
              ) : (
                <MessageBar intent="info">
                  <MessageBarBody>
                    You&apos;re seeing the aggregate view. Listing the individual users behind these figures needs the
                    opt-in <strong>LicenceActivity.ReadUsers</strong> role, which an administrator can grant in Entra.
                  </MessageBarBody>
                </MessageBar>
              )}
            </div>
          )}
        </>
      )}
    </div>
  );
}

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  makeStyles,
  tokens,
  Badge,
  Title3,
  Body1,
  Text,
  Card,
  Select,
  Button,
  Tab,
  TabList,
  Tooltip,
  MessageBar,
  MessageBarBody,
  type SelectTabEventHandler,
} from '@fluentui/react-components';
import { ArrowDownload16Regular } from '@fluentui/react-icons';
import { fetchAvailability, fetchOverview, downloadExport } from '../api/licenceActivityApi';
import { EN_CATALOG, useT, type TranslationKey } from '../i18n';
import type {
  DateRange,
  LicenceActivityAvailability,
  LicenceActivityOverview,
} from '../types/licenceActivity';
import Spinner from '../components/Spinner';
import DateRangeControl from '../components/licenceActivity/DateRangeControl';
import DataSourceSummary from '../components/licenceActivity/DataSourceSummary';
import OverviewSummary from '../components/licenceActivity/OverviewSummary';
import SkuAssignments from '../components/licenceActivity/SkuAssignments';
import SelectedLicenceBar from '../components/licenceActivity/SelectedLicenceBar';
import WorkloadDistributions from '../components/licenceActivity/WorkloadDistributions';
import DemographicBreakdown from '../components/licenceActivity/DemographicBreakdown';
import { demographicName } from '../components/licenceActivity/DemographicBreakdown';
import UsersDrillDown from '../components/licenceActivity/UsersDrillDown';
import ApiErrorBar, { describeError } from '../components/licenceActivity/ApiErrorBar';
import { presetRange } from '../components/licenceActivity/dateRange';
import { formatCount } from '../components/licenceActivity/format';
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
  titleRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  intro: {
    marginTop: '8px',
    maxWidth: '820px',
  },
  previewNote: {
    marginTop: '6px',
    maxWidth: '820px',
    color: tokens.colorNeutralForeground3,
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
  // A tab panel's own vertical rhythm. The panel <div> itself carries no `display` override so the
  // `hidden` attribute can hide inactive panels (a `display: flex` on the panel would beat the UA
  // `[hidden] { display: none }` rule and leak every panel onto the page at once).
  panelInner: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
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
 * The report's top-level sections, split into tabs so the default view answers "what is my licence
 * usage like?" without scrolling past the detail. The reporting window, demographic filters and the
 * data-source summary stay above the tabs because they scope every one of them.
 */
type LaTab = 'overview' | 'byService' | 'byDemographic' | 'people';

export function serverMessageText(t: ReturnType<typeof useT>, message: string): string {
  const matches: [TranslationKey, string][] = [
    ['licenceActivity.note.userMetadataRequired', EN_CATALOG['licenceActivity.note.userMetadataRequired']],
    ['licenceActivity.note.privacy', EN_CATALOG['licenceActivity.note.privacy']],
    ['licenceActivity.note.assignmentCaveat', EN_CATALOG['licenceActivity.note.assignmentCaveat']],
    ['licenceActivity.note.interpretationCaveat', EN_CATALOG['licenceActivity.note.interpretationCaveat']],
    ['licenceActivity.note.activityMethod', EN_CATALOG['licenceActivity.note.activityMethod']],
  ];
  const match = matches.find(([, english]) => english === message);
  return match ? t(match[0]) : message;
}

/**
 * Licence activity report (issues #436 / #437).
 *
 * Answers, for a business leader or M365 admin: which licences are assigned, and how much are the
 * people who hold them actually using each Microsoft 365 service? It is deliberately an ACTIVITY
 * report - no blended "productivity" score, no "remove this licence" button; it surfaces the evidence
 * and leaves the decision with the reader.
 *
 * Everyone who can open the portal sees the whole report, including the per-person lists. There is no
 * second permission level.
 */
export default function LicenceActivityPage() {
  const styles = useStyles();
  const t = useT();

  const [availability, setAvailability] = useState<LicenceActivityAvailability | null>(null);
  const [availabilityError, setAvailabilityError] = useState<unknown>(null);
  const [availabilityLoading, setAvailabilityLoading] = useState(true);

  // The reporting window lives here, so it is preserved across demographic-filter and licence
  // changes - only the date control (or a preset click) ever changes it. Defaults to 28 days.
  const [range, setRange] = useState<DateRange>(() => presetRange(28));
  const [departmentId, setDepartmentId] = useState<number | null>(null);
  const [countryId, setCountryId] = useState<number | null>(null);

  const [overviewResult, setOverviewResult] = useState<{
    key: string;
    generation: number;
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

  // Which top-level tab is showing. Kept out of the overview scope key so it survives a date/filter
  // change - an admin reading the People tab stays on it when they widen the window.
  const [tab, setTab] = useState<LaTab>('overview');
  const onTabSelect: SelectTabEventHandler = (_e: unknown, data: { value: unknown }) => setTab(data.value as LaTab);

  const overviewSeqRef = useRef(0);

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
  }, []);

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
        if (mySeq === overviewSeqRef.current)
          setOverviewResult({ key: overviewKey, generation: overviewReloadKey, data: o, error: null });
      })
      .catch((err) => {
        if (mySeq !== overviewSeqRef.current || controller.signal.aborted) return;
        if (err instanceof DOMException && err.name === 'AbortError') return;
        setOverviewResult({ key: overviewKey, generation: overviewReloadKey, data: null, error: err });
      });

    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [overviewKey, overviewReloadKey]);

  // Only surface an overview that belongs to the CURRENT scope key. A filter change hides the previous
  // overview (and its export id) on this render, before the new request starts.
  const overviewBelongs = overviewResult !== null && overviewResult.key === overviewKey;
  const overviewCurrent = overviewBelongs && overviewResult.generation === overviewReloadKey;
  const overview = overviewBelongs ? overviewResult!.data : null;
  const overviewError = overviewCurrent ? overviewResult!.error : null;
  // Keep the same-scope drill-down mounted to preserve its page, but do not export a retiring generation.
  const overviewLoading = overviewKey !== null && !overviewCurrent;

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

  // The correct response to figures the server no longer holds (an export 410, or a users 410): pull a
  // fresh set in place. We drop the stale users id and error, force the drill-down to re-fetch (its
  // params are unchanged, so the licence/workload/page/filters are preserved), and reload the
  // overview. The overview often comes back under the SAME cached id, which is exactly why clearing is
  // explicit here rather than relying on the id-changed effect. It deliberately does NOT re-export: an
  // export must be an explicit action, never a silent re-query of freshly loaded figures.
  const refreshSnapshots = useCallback(() => {
    setExportError(null);
    setUsersId(null);
    setUsersRefreshToken((t) => t + 1);
    setOverviewReloadKey((k) => k + 1);
  }, []);

  const selectedLicence = useMemo(
    () => overview?.licences.find((s) => s.licenceTypeId === selectedLicenceTypeId) ?? null,
    [overview, selectedLicenceTypeId],
  );

  // Attach the current user list to the export whenever the reader is looking at a licence's list;
  // otherwise the workbook is totals-only.
  const exportUsersId = selectedLicence ? usersId ?? undefined : undefined;

  const onExport = async (): Promise<void> => {
    if (!overview || overviewLoading) return;
    setExporting(true);
    setExportError(null);
    try {
      await downloadExport({ overviewId: overview.snapshotId, usersId: exportUsersId });
    } catch (err) {
      setExportError(err);
    } finally {
      setExporting(false);
    }
  };

  const exportExpired = describeError(exportError, '').kind === 'expired';

  return (
    <div>
      <div className={styles.header}>
        <div>
          <div className={styles.titleRow}>
            <Title3>{t('licenceActivity.page.title')}</Title3>
            <Badge appearance="tint" color="brand" size="medium">
              {t('licenceActivity.page.preview')}
            </Badge>
          </div>
          <Body1 block className={styles.intro}>{t('licenceActivity.page.intro')}</Body1>
          <Text role="note" block size={200} className={styles.previewNote}>{t('licenceActivity.page.previewNote')}</Text>
        </div>
      </div>

      {availabilityLoading && (
        <div className={styles.center}>
          <Spinner size={80} label={t('licenceActivity.page.checkingAvailability')} />
        </div>
      )}

      {availabilityError != null && (
        <ApiErrorBar
          error={availabilityError}
          fallback={t('licenceActivity.page.availabilityFailed')}
          onRetry={() => window.location.reload()}
          retryLabel={t('licenceActivity.page.reload')}
        />
      )}

      {availability && !availability.available && (
        <MessageBar intent="info" style={{ marginTop: '16px' }}>
          <MessageBarBody>
            {t('licenceActivity.page.notAvailable')}
            {availability.messages.length > 0 && (
              <ul style={{ margin: '6px 0 0 0', paddingInlineStart: '20px' }}>
                {availability.messages.map((m) => (
                  <li key={m}>{serverMessageText(t, m)}</li>
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
                    <li key={m}>{serverMessageText(t, m)}</li>
                  ))}
                </ul>
              </MessageBarBody>
            </MessageBar>
          )}

          <Card className={styles.controlsCard}>
            <div className={styles.controlRow}>
              <div className={styles.field}>
                <Text size={200} className={styles.fieldLabel}>
                  {t('licenceActivity.page.reportingWindow')}
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
                  {t('licenceActivity.common.department')}
                </Text>
                <Select
                  value={departmentId == null ? '' : String(departmentId)}
                  aria-label={t('licenceActivity.page.filterByDepartment')}
                  disabled={departmentOptions.options.length === 0}
                  onChange={(_e: unknown, d: { value: string }) => setDepartmentId(d.value === '' ? null : Number(d.value))}
                >
                  <option value="">{t('licenceActivity.page.allDepartments')}</option>
                  {departmentOptions.options.map((dept) => (
                    <option key={dept.id} value={dept.id}>
                      {demographicName(t, dept)}
                    </option>
                  ))}
                </Select>
                {departmentOptions.truncated && (
                  <Text size={100} className={styles.muted}>
                    {t('licenceActivity.page.recentOptionsHidden', { count: formatCount(DEMOGRAPHIC_OPTION_CAP) })}
                  </Text>
                )}
              </div>

              <div className={styles.field}>
                <Text size={200} className={styles.fieldLabel}>
                  {t('licenceActivity.common.country')}
                </Text>
                <Select
                  value={countryId == null ? '' : String(countryId)}
                  aria-label={t('licenceActivity.page.filterByCountry')}
                  disabled={countryOptions.options.length === 0}
                  onChange={(_e: unknown, d: { value: string }) => setCountryId(d.value === '' ? null : Number(d.value))}
                >
                  <option value="">{t('licenceActivity.page.allCountries')}</option>
                  {countryOptions.options.map((country) => (
                    <option key={country.id} value={country.id}>
                      {demographicName(t, country)}
                    </option>
                  ))}
                </Select>
                {countryOptions.truncated && (
                  <Text size={100} className={styles.muted}>
                    {t('licenceActivity.page.recentOptionsHidden', { count: formatCount(DEMOGRAPHIC_OPTION_CAP) })}
                  </Text>
                )}
              </div>

              <div style={{ flexGrow: 1 }} />

              <div className={styles.exportWrap}>
                <Tooltip
                  relationship="description"
                  content={
                    overview && !overviewLoading
                      ? exportUsersId
                        ? t('licenceActivity.page.exportSummaryAndPeopleTooltip')
                        : t('licenceActivity.page.exportSummaryTooltip')
                      : t('licenceActivity.page.exportUnavailableTooltip')
                  }
                >
                  <Button
                    appearance="primary"
                    icon={<ArrowDownload16Regular />}
                    disabled={!overview || overviewLoading || exporting}
                    onClick={onExport}
                  >
                    {exporting ? t('licenceActivity.page.exporting') : t('licenceActivity.page.exportToExcel')}
                  </Button>
                </Tooltip>
              </div>
            </div>

            {exportError != null && (
              <ApiErrorBar
                error={exportError}
                fallback={t('licenceActivity.page.exportFailed')}
                onRetry={exportExpired ? refreshSnapshots : onExport}
                retryLabel={exportExpired ? t('licenceActivity.common.refresh') : t('licenceActivity.page.tryAgain')}
              />
            )}
          </Card>

          {overviewLoading && (
            <div className={styles.center}>
              <Spinner size={80} label={t('licenceActivity.page.loading')} />
            </div>
          )}

          {overviewError != null && (
            <div style={{ marginTop: '16px' }}>
              <ApiErrorBar
                error={overviewError}
                fallback={t('licenceActivity.page.overviewFailed')}
                onRetry={reloadOverview}
              />
            </div>
          )}

          {overview && (
            <div className={styles.stack}>
              <DataSourceSummary
                coverage={overview.coverage}
                generatedUtc={overview.generatedUtc}
                expiresUtc={overview.expiresUtc}
              />

              {overview.messages.length > 0 && (
                <MessageBar intent="info">
                  <MessageBarBody>
                    <ul style={{ margin: 0, paddingInlineStart: '20px' }}>
                      {overview.messages.map((m) => (
                        <li key={m}>{serverMessageText(t, m)}</li>
                      ))}
                    </ul>
                  </MessageBarBody>
                </MessageBar>
              )}

              <TabList selectedValue={tab} onTabSelect={onTabSelect}>
                <Tab id="la-tab-overview" value="overview" aria-controls="la-panel-overview">
                  {t('licenceActivity.page.tabOverview')}
                </Tab>
                <Tab id="la-tab-byService" value="byService" aria-controls="la-panel-byService">
                  {t('licenceActivity.page.tabByService')}
                </Tab>
                <Tab id="la-tab-byDemographic" value="byDemographic" aria-controls="la-panel-byDemographic">
                  {t('licenceActivity.page.tabByDemographic')}
                </Tab>
                <Tab id="la-tab-people" value="people" aria-controls="la-panel-people">
                  {t('licenceActivity.page.tabPeople')}
                </Tab>
              </TabList>

              {/* Overview: the headline figures plus the assignments table, which doubles as the
                  licence picker for the By service and People tabs. Panels stay mounted (toggled with
                  `hidden`) so the drill-down keeps its page, workload and search when tabs change. */}
              <div
                role="tabpanel"
                id="la-panel-overview"
                aria-labelledby="la-tab-overview"
                hidden={tab !== 'overview'}
              >
                <div className={styles.panelInner}>
                  <OverviewSummary
                    distinctAssignedUsers={overview.distinctAssignedUsers}
                    licenceCount={overview.licences.length}
                    selectedLicence={selectedLicence}
                  />
                  <SkuAssignments
                    licences={overview.licences}
                    selectedLicenceTypeId={selectedLicenceTypeId}
                    onSelect={setSelectedLicenceTypeId}
                  />
                </div>
              </div>

              {/* By service: the selected licence's five workload distributions. */}
              <div
                role="tabpanel"
                id="la-panel-byService"
                aria-labelledby="la-tab-byService"
                hidden={tab !== 'byService'}
              >
                <div className={styles.panelInner}>
                  <div className={styles.sectionHead}>
                    <Text weight="semibold" size={500}>
                      {t('licenceActivity.page.activityByService')}
                    </Text>
                    <Text size={200} className={styles.muted}>
                      {t('licenceActivity.page.activityByServiceSubtitle')}
                    </Text>
                  </div>
                  {selectedLicence ? (
                    <>
                      <SelectedLicenceBar
                        licences={overview.licences}
                        selectedLicenceTypeId={selectedLicenceTypeId}
                        onSelect={setSelectedLicenceTypeId}
                      />
                      <WorkloadDistributions workloads={selectedLicence.workloads} />
                    </>
                  ) : (
                    <Card>
                      <Text className={styles.muted}>
                        {t('licenceActivity.page.chooseLicenceOverview')}
                      </Text>
                    </Card>
                  )}
                </div>
              </div>

              {/* By department & country: the two aggregate demographic breakdowns. */}
              <div
                role="tabpanel"
                id="la-panel-byDemographic"
                aria-labelledby="la-tab-byDemographic"
                hidden={tab !== 'byDemographic'}
              >
                <div className={styles.panelInner}>
                  <div className={styles.sectionHead}>
                    <Text weight="semibold" size={500}>
                      {t('licenceActivity.page.activityByDemographic')}
                    </Text>
                    <Text size={200} className={styles.muted}>
                      {t('licenceActivity.page.activityByDemographicSubtitle')}
                    </Text>
                  </div>
                  {overview.demographicsTruncated && (
                    <Text size={200} className={styles.muted}>
                      {t('licenceActivity.page.demographicCapped')}
                    </Text>
                  )}
                  {overview.departments.length > 0 || overview.countries.length > 0 ? (
                    <div style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
                      <DemographicBreakdown
                        title={t('licenceActivity.page.byDepartment')}
                        segmentLabel={t('licenceActivity.common.department')}
                        rows={overview.departments}
                        truncated={overview.demographicsTruncated}
                      />
                      <DemographicBreakdown
                        title={t('licenceActivity.page.byCountry')}
                        segmentLabel={t('licenceActivity.common.country')}
                        rows={overview.countries}
                        truncated={overview.demographicsTruncated}
                      />
                    </div>
                  ) : (
                    <Card>
                      <Text className={styles.muted}>
                        {t('licenceActivity.page.noDemographicBreakdown')}
                      </Text>
                    </Card>
                  )}
                </div>
              </div>

              {/* People: the per-licence drill-down (most/least active and the browse table). */}
              <div
                role="tabpanel"
                id="la-panel-people"
                aria-labelledby="la-tab-people"
                hidden={tab !== 'people'}
              >
                <div className={styles.panelInner}>
                  <div className={styles.sectionHead}>
                    <Text weight="semibold" size={500}>
                      {t('licenceActivity.page.peopleHoldingLicence')}
                    </Text>
                    <Text size={200} className={styles.muted}>
                      {t('licenceActivity.page.peopleSubtitle')}
                    </Text>
                  </div>
                  {selectedLicence ? (
                    <>
                      <SelectedLicenceBar
                        licences={overview.licences}
                        selectedLicenceTypeId={selectedLicenceTypeId}
                        onSelect={setSelectedLicenceTypeId}
                      />
                      <UsersDrillDown
                        key={selectedLicence.licenceTypeId}
                        overviewId={overview.snapshotId}
                        overviewScope={overviewKey ?? ''}
                        licence={selectedLicence}
                        coverage={overview.coverage}
                        onUsersSnapshot={handleUsersSnapshot}
                        onRefreshOverview={refreshSnapshots}
                        refreshToken={usersRefreshToken}
                      />
                    </>
                  ) : (
                    <Card>
                      <Text className={styles.muted}>
                        {t('licenceActivity.page.selectLicenceForPeople')}
                      </Text>
                    </Card>
                  )}
                </div>
              </div>
            </div>
          )}
        </>
      )}
    </div>
  );
}

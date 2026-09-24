import { useEffect, useRef, useState } from 'react';
import {
  makeStyles,
  tokens,
  Title3,
  Body1,
  Text,
  Button,
  Card,
  Select,
  Tab,
  TabList,
  Tooltip,
  MessageBar,
  MessageBarBody,
  Link,
  Accordion,
  AccordionHeader,
  AccordionItem,
  AccordionPanel,
  type SelectTabEventHandler,
} from '@fluentui/react-components';
import { ArrowDownload16Regular } from '@fluentui/react-icons';
import {
  fetchAdoptionAvailability,
  fetchAdoptionFilters,
  fetchAdoptionSql,
  fetchAdoptionSummary,
  workbookExportUrl,
} from '../api/copilotAdoptionApi';
import type {
  AccountabilityRollupRow,
  AdoptionFilterOptions,
  CopilotAdoptionAvailability,
  CopilotAdoptionSummary,
} from '../types/copilotAdoption';
import Spinner from '../components/Spinner';
import SqlPopover from '../components/SqlPopover';
import TimeSeriesChart from '../components/charts/TimeSeriesChart';
import CategoryBarChart from '../components/charts/CategoryBarChart';
import DonutChart from '../components/charts/DonutChart';
import TreemapChart from '../components/charts/TreemapChart';
import StackedAreaChart from '../components/charts/StackedAreaChart';
import GaugeRing, { bandTone, describeBands } from '../components/charts/GaugeRing';
import RadarChart from '../components/charts/RadarChart';
import AdoptionFunnel from '../components/copilotAdoption/AdoptionFunnel';
import LicensedUsersPanel from '../components/copilotAdoption/LicensedUsersPanel';
import CoworkPanel from '../components/copilotAdoption/CoworkPanel';
import OpportunitiesPanel from '../components/copilotAdoption/OpportunitiesPanel';
import HabitStrip from '../components/copilotAdoption/HabitStrip';
import IntensityScatter from '../components/copilotAdoption/IntensityScatter';
import ActionPlan from '../components/copilotAdoption/ActionPlan';
import AgentsPanel from '../components/copilotAdoption/AgentsPanel';
import UnlicensedPanel from '../components/copilotAdoption/UnlicensedPanel';
import ResourceTypesPanel from '../components/copilotAdoption/ResourceTypesPanel';
import EmailDomainPanel from '../components/copilotAdoption/EmailDomainPanel';
import { ConcentrationBar, CombinedSegmentTable } from '../components/copilotAdoption/CombinedViews';
import InfoTip from '../components/shared/InfoTip';
import PrintButton from '../components/shared/PrintButton';
import { PRINT_ROW_LIMIT } from '../components/shared/printPreparation';
import DismissibleWarnings from '../components/shared/DismissibleWarnings';
import { SegmentTable, BAND_COLOUR_LIST } from '../components/copilotAdoption/adoptionShared';
import { KpiGrid, formatCount, formatDate, formatPct, weightSharePct } from '../components/shared/KpiGrid';
import type { KpiDefinition } from '../components/shared/KpiGrid';
import { activeLocale, formatNumber, plural, useT, useTNode, type TFunction, type TranslationKey } from '../i18n';
import { adoptionBandLabel, availabilityMessages, scoreProfileLabel } from '../components/copilotAdoption/serverText';
import {
  compactHoursRange,
  projectCoworkTimeSaved,
  projectLicenceTimeSaved,
  timeSavedExportParams,
  useTimeSavedAssumptions,
  type TimeSavedAssumptions,
} from '../components/copilotAdoption/coworkTimeSaved';

const WINDOW_OPTIONS: { value: number; labelKey: TranslationKey }[] = [
  { value: 7, labelKey: 'copilotAdoption.page.window.last7Days' },
  { value: 28, labelKey: 'copilotAdoption.page.window.last28Days' },
  { value: 90, labelKey: 'copilotAdoption.page.window.last90Days' },
  { value: 180, labelKey: 'copilotAdoption.page.window.last180Days' },
];

type AdoptionTab = 'executive' | 'analyst' | 'licensed' | 'cowork' | 'unlicensed' | 'agents' | 'opportunities' | 'method';

/**
 * The tab strip, in order.
 *
 * Single source of truth so the strip and the print caption cannot drift: the caption names the
 * view on a printout, where the tab strip itself is hidden and the reader has no other way to tell
 * which of the eight views the sheet in their hand is.
 */
const TAB_LABEL_KEYS: Record<AdoptionTab, TranslationKey> = {
  executive: 'copilotAdoption.page.tab.executive',
  analyst: 'copilotAdoption.page.tab.analyst',
  licensed: 'copilotAdoption.page.tab.licensed',
  cowork: 'copilotAdoption.page.tab.cowork',
  unlicensed: 'copilotAdoption.page.tab.unlicensed',
  agents: 'copilotAdoption.page.tab.agents',
  opportunities: 'copilotAdoption.page.tab.opportunities',
  method: 'copilotAdoption.page.tab.method',
};

/** Plain-English names for the sections the server could not narrow to one email domain. */
const UNSCOPED_SECTION_LABEL_KEYS: Record<string, TranslationKey> = {
  usageByApp: 'copilotAdoption.page.unscoped.usageByApp',
  topResourceTypes: 'copilotAdoption.page.unscoped.topResourceTypes',
  weeklyTrend: 'copilotAdoption.page.unscoped.weeklyTrend',
  agents: 'copilotAdoption.page.unscoped.agents',
  purchasedSeats: 'copilotAdoption.page.unscoped.purchasedSeats',
  coworkCredits: 'copilotAdoption.page.unscoped.coworkCredits',
};

/**
 * Lists the tenant-wide sections in a sentence.
 *
 * Says which ones rather than a bare count, because "four sections are tenant-wide" leaves the
 * reader to work out whether the chart in front of them is one of them.
 */
function describeUnscopedSections(t: TFunction, sections: string[]): string {
  const labels = sections.map((s) => {
    const key = UNSCOPED_SECTION_LABEL_KEYS[s];
    return key ? t(key) : s;
  });
  return new Intl.ListFormat(activeLocale(), { style: 'long', type: 'conjunction' }).format(labels);
}

function hasTrendGaps(series: { points: { value: number | null }[] }[]): boolean {
  return series.some((s) => s.points.some((p) => p.value === null));
}

const MICROSOFT_COPILOT_USAGE_REPORT_FAQ_URL =
  'https://learn.microsoft.com/en-us/microsoft-365/admin/activity-reports/microsoft-365-copilot-usage?view=o365-worldwide#whats-the-difference-between-the-user-activity-table-and-audit-log';
const MICROSOFT_COPILOT_USAGE_REPORT_API_URL =
  'https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/api/admin-settings/reports/copilotreportroot-getmicrosoft365copilotusageuserdetail';

const useStyles = makeStyles({
  header: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
  },
  controls: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  intro: {
    marginTop: '8px',
    maxWidth: '780px',
  },
  // Paper only - see the `data-print` contract in index.css. The filter controls and the tab strip
  // are both hidden when printing, which would otherwise leave a printout that does not say which
  // view, which period or which email domain it is a report of.
  printScope: {
    display: 'none',
    marginTop: '8px',
    color: tokens.colorNeutralForeground2,
  },
  subTabs: {
    marginTop: '16px',
  },
  stack: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    marginTop: '16px',
  },
  twoUp: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))',
    gap: '16px',
  },
  sectionHead: {
    display: 'flex',
    alignItems: 'baseline',
    gap: '12px',
    marginTop: '12px',
    paddingBottom: '6px',
    borderBottomWidth: '2px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorBrandStroke1,
  },
  sectionIndex: {
    color: tokens.colorBrandForeground1,
    fontVariantNumeric: 'tabular-nums',
  },
  cardHead: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '12px',
  },
  cardTools: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    flexShrink: 0,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  cardBody: {
    marginTop: '10px',
  },
  gauges: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '28px',
    justifyContent: 'space-around',
    alignItems: 'flex-start',
  },
  method: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    color: tokens.colorNeutralForeground2,
    maxWidth: '860px',
  },
  formula: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: '12px',
    lineHeight: '18px',
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusSmall,
    padding: '10px 12px',
    whiteSpace: 'pre-wrap',
    color: tokens.colorNeutralForeground1,
  },
  skuTable: {
    width: '100%',
    borderCollapse: 'collapse',
  },
  skuCell: {
    padding: '6px 10px',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke3,
    textAlign: 'left',
  },
});

/**
 * The numbered heading that opens each act of a view ("1. Where we stand").
 *
 * One component rather than seven copies of the markup, because each heading also carries the page
 * break that makes the printed report readable: every act starts a fresh sheet, and no heading is
 * left stranded at the foot of a page with its content overleaf. Act 1 deliberately does not break
 * - it belongs with the KPI tiles on the report's front page.
 */
function SectionHead({ index, title, blurb }: { index: number; title: string; blurb: string }) {
  const styles = useStyles();
  return (
    <div className={styles.sectionHead} data-print={index === 1 ? 'keep-with-next' : 'page-break'}>
      <Text weight="semibold" size={500}>
        <span className={styles.sectionIndex}>{index}.</span> {title}
      </Text>
      <Text size={200} className={styles.muted}>
        {blurb}
      </Text>
    </div>
  );
}

/**
 * The Copilot Adoption area.
 *
 * Answers the two questions that decide Copilot spend: which licensed users are not getting value
 * from their seat (as a graded score, not a yes/no), and which unlicensed heavy Microsoft 365 users
 * have the strongest case for one. Both lists export to CSV so they can be handed to department
 * leads or attached to a licence request.
 *
 * Deliberately built for an executive reader: every headline number has its definition on the page,
 * the segment breakdowns show absolute counts next to percentages, and the "How these numbers are
 * calculated" tab spells out the formula and the exact SKUs that were counted as Copilot seats -
 * because the first question anyone asks about a number like this is "where did that come from?".
 */
export default function CopilotAdoptionPage() {
  const styles = useStyles();
  const t = useT();
  const tNode = useTNode();

  const [availability, setAvailability] = useState<CopilotAdoptionAvailability | null>(null);
  const [availabilityError, setAvailabilityError] = useState<string | null>(null);
  const [windowDays, setWindowDays] = useState(28);
  // The email domain every visual on the page is narrowed to, or null for the whole tenant.
  //
  // A page-wide filter rather than a per-panel one: a tenant carrying several verified domains is
  // usually several companies, and "how is the business we acquired doing" is a question about the
  // whole report, not about one table. The server re-scores the cached analysis for the domain, so
  // the figures are recomputed rather than merely hidden.
  const [emailDomain, setEmailDomain] = useState<string | null>(null);
  const [tab, setTab] = useState<AdoptionTab>('executive');
  // Set when the user drills through from the enablement plan, so the licensed-user list they land
  // on is pre-filtered to exactly the group the plan counted. Cleared when they choose a tab
  // themselves - otherwise a filter they never asked for reappears every time they come back.
  const [drillAction, setDrillAction] = useState<string | undefined>(undefined);

  const [summary, setSummary] = useState<CopilotAdoptionSummary | null>(null);
  const [summaryLoading, setSummaryLoading] = useState(true);
  const [summaryError, setSummaryError] = useState<string | null>(null);
  const [filterOptions, setFilterOptions] = useState<AdoptionFilterOptions | null>(null);
  const [sql, setSql] = useState<Record<string, string> | null>(null);
  const lastSummaryScope = useRef<string | null>(null);
  // The reader's own time-saved figures, if any, so the Excel report models the hours on screen.
  const timeSaved = useTimeSavedAssumptions(summary);

  useEffect(() => {
    let cancelled = false;
    fetchAdoptionAvailability()
      .then((a) => {
        if (!cancelled) setAvailability(a);
      })
      .catch((e: unknown) => {
        if (!cancelled) {
          setAvailabilityError(e instanceof Error ? e.message : t('copilotAdoption.page.errors.checkAvailability'));
        }
      });
    return () => {
      cancelled = true;
    };
  }, [t]);

  useEffect(() => {
    if (!availability?.available) {
      setSummaryLoading(false);
      return;
    }

    let cancelled = false;
    const controller = new AbortController();
    // Keyed on the WHOLE scope, not just the period. Leaving a stale summary in place while a new
    // scope loads kept the Excel button pointing at one population and enabled against another -
    // and if the new request then failed, the page went on rendering the previous organisation's
    // figures underneath a heading naming the newly selected one.
    const scopeKey = `${windowDays}::${emailDomain ?? ''}`;
    if (lastSummaryScope.current !== scopeKey) {
      setSummary(null);
    }
    setSummaryLoading(true);
    setSummaryError(null);

    fetchAdoptionSummary(windowDays, undefined, controller.signal, emailDomain)
      .then((s) => {
        if (!cancelled) {
          lastSummaryScope.current = scopeKey;
          setSummary(s);
        }
      })
      .catch((e: unknown) => {
        if (cancelled || controller.signal.aborted) return;
        setSummaryError(e instanceof Error ? e.message : t('copilotAdoption.page.errors.loadSummary'));
      })
      .finally(() => {
        if (!cancelled) setSummaryLoading(false);
      });

    // The filter lists and the SQL come from the same cached analysis, so these are cheap follow-ups
    // rather than extra work. Their failure is not worth surfacing - the page works without them.
    //
    // Deliberately NOT narrowed by emailDomain: this is the list the domain filter is chosen FROM,
    // so narrowing it would leave the current selection as the only option and strand the user on it.
    fetchAdoptionFilters(windowDays, undefined, controller.signal)
      .then((f) => {
        if (!cancelled) setFilterOptions(f);
      })
      .catch(() => undefined);

    fetchAdoptionSql(windowDays, undefined, controller.signal)
      .then((s) => {
        if (!cancelled) setSql(s);
      })
      .catch(() => undefined);

    return () => {
      cancelled = true;
      // Aborting matters now that a 202 makes these poll: a bare `cancelled` flag would only suppress
      // the state update and leave three polling loops running for up to ten minutes after the period
      // changed or the page unmounted.
      controller.abort();
    };
  }, [availability, windowDays, emailDomain]);

  const onTabSelect: SelectTabEventHandler = (_e: unknown, data: { value: unknown }) => {
    setDrillAction(undefined);
    setTab(data.value as AdoptionTab);
  };

  const drillToAction = (code: string) => {
    setDrillAction(code);
    setTab('licensed');
  };

  const showLicensedDetails = () => {
    setDrillAction(undefined);
    setTab('licensed');
  };

  const showOpportunityDetails = () => {
    setDrillAction(undefined);
    setTab('opportunities');
  };

  /** Where a headline tile's link takes the reader - the tab that explains the figure. */
  const openTab = (next: AdoptionTab) => {
    setDrillAction(undefined);
    setTab(next);
  };

  return (
    <div>
      <div className={styles.header}>
        <div>
          <Title3>{t('copilotAdoption.page.title')}</Title3>
          <Body1 block className={styles.intro}>{t('copilotAdoption.page.intro')}</Body1>
        </div>
        <div className={styles.controls} data-print="hide">
          <Text size={200} className={styles.muted}>{t('copilotAdoption.page.controls.periodLabel')}</Text>
          <Select
            value={String(windowDays)}
            onChange={(_e: unknown, d: { value: string }) => setWindowDays(Number(d.value))}
            aria-label={t('copilotAdoption.page.controls.reportingPeriodAria')}
          >
            {WINDOW_OPTIONS.map((o) => (
              <option key={o.value} value={o.value}>
                {t(o.labelKey)}
              </option>
            ))}
          </Select>

          {/* Only offered once more than one domain exists. On a single-domain tenant the control
              would be a drop-down with exactly one real choice, which reads as a missing feature. */}
          {availability?.available && (filterOptions?.emailDomains?.length ?? 0) > 1 && (
            <>
              <Text size={200} className={styles.muted}>{t('copilotAdoption.page.controls.emailDomainLabel')}</Text>
              <Select
                value={emailDomain ?? ''}
                onChange={(_e: unknown, d: { value: string }) => setEmailDomain(d.value || null)}
                aria-label={t('copilotAdoption.page.controls.emailDomainAria')}
              >
                <option value="">{t('copilotAdoption.page.controls.allDomainsOption')}</option>
                {(filterOptions?.emailDomains ?? []).map((d) => (
                  <option key={d} value={d}>
                    {d}
                  </option>
                ))}
              </Select>
            </>
          )}
          {availability?.available && (
            <PrintButton
              tooltip={t('copilotAdoption.page.controls.printTooltip', {
                v0: t(TAB_LABEL_KEYS[tab]),
                limit: formatCount(PRINT_ROW_LIMIT),
              })}
            />
          )}
          {availability?.available && (
            <Tooltip
              relationship="description"
              content={
                summary
                  ? t('copilotAdoption.page.controls.excelTooltipReady')
                  : t('copilotAdoption.page.controls.excelTooltipLoading')
              }
            >
              {/* Disabled until the summary is in. The export is an <a href> download, so it cannot poll:
                  clicking it before the analysis is cached makes the browser wait out the whole run, which
                  on a large tenant is longer than the platform allows. See issue #360. */}
              <Button
                appearance="primary"
                icon={<ArrowDownload16Regular />}
                as="a"
                href={summary ? workbookExportUrl(windowDays, undefined, emailDomain, timeSavedExportParams(timeSaved)) : undefined}
                disabled={!summary}
              >{t('copilotAdoption.page.controls.excelReport')}</Button>
            </Tooltip>
          )}
        </div>
      </div>

      {availability?.available && (
        <div className={styles.printScope} data-print="only">
          <Text size={200}>
            {t(TAB_LABEL_KEYS[tab])}
            {' \u00b7 '}
            {WINDOW_OPTIONS.find((o) => o.value === windowDays)?.labelKey
              ? t(WINDOW_OPTIONS.find((o) => o.value === windowDays)!.labelKey)
              : t('copilotAdoption.page.print.lastDays', { v0: windowDays })}
            {summary && <> {t('copilotAdoption.page.print.dateRange', { v0: formatDate(summary.fromUtc), v1: formatDate(summary.toUtc) })}</>}
            {' \u00b7 '}
            {emailDomain ?? t('copilotAdoption.page.print.allEmailDomains')}
            {summary && <> · {t('copilotAdoption.page.print.generatedDate', { v0: formatDate(summary.generatedUtc) })}</>}
          </Text>
        </div>
      )}

      {availabilityError && (
        <MessageBar intent="error" style={{ marginTop: '16px' }}>
          <MessageBarBody>{availabilityError}</MessageBarBody>
        </MessageBar>
      )}

      {availability && !availability.available && (
        <MessageBar intent="info" style={{ marginTop: '16px' }}>
          <MessageBarBody>{t('copilotAdoption.page.unavailable.message')}<ul style={{ margin: '6px 0 0 0', paddingInlineStart: '20px' }}>
              {availabilityMessages(t, availability).map((m) => (
                <li key={m}>{m}</li>
              ))}
            </ul>
          </MessageBarBody>
        </MessageBar>
      )}

      {availability?.available && (
        <>
          {availability.messages.length > 0 && (
            <DismissibleWarnings messages={availabilityMessages(t, availability)} style={{ marginTop: '16px' }} />
          )}

          <div className={styles.subTabs} data-print="hide">
            <TabList selectedValue={tab} onTabSelect={onTabSelect}>
              {(Object.keys(TAB_LABEL_KEYS) as AdoptionTab[]).map((value) => (
                <Tab key={value} value={value}>
                  {t(TAB_LABEL_KEYS[value])}
                </Tab>
              ))}
            </TabList>
          </div>

          {summaryLoading && (
            <div style={{ textAlign: 'center', padding: '32px' }}>
              <Spinner size={80} label={t('copilotAdoption.page.loading.label')} />
            </div>
          )}

          {summaryError && (
            <MessageBar intent="error" style={{ marginTop: '16px' }}>
              <MessageBarBody>{summaryError}</MessageBarBody>
            </MessageBar>
          )}

          {!summaryLoading && summary && (
            <div className={styles.stack} data-print="flow">
              {summary.figuresIncomplete && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    {tNode('copilotAdoption.page.incomplete.message', {
                      warning: <strong>{t('copilotAdoption.page.incomplete.warning')}</strong>,
                      missing:
                        summary.incompleteReasons.length > 0
                          ? `${t('copilotAdoption.page.incomplete.missingPrefix')} ${summary.incompleteReasons.join(', ')}. `
                          : '',
                    })}
                  </MessageBarBody>
                </MessageBar>
              )}

              {summary.warnings.length > 0 && <DismissibleWarnings messages={summary.warnings} />}

              {/* Stated on screen, every time. A dashboard silently showing one subsidiary is the
                  fastest way to get a licence decision wrong, and the domain drop-down is easy to
                  miss once the page has scrolled. */}
              {summary.scopedEmailDomain && (
                <MessageBar intent="info">
                  <MessageBarBody>
                    {tNode('copilotAdoption.page.scopeBanner.message', {
                      scope: <strong>{t('copilotAdoption.page.scopeBanner.scope', { domain: summary.scopedEmailDomain })}</strong>,
                      unscoped:
                        (summary.unscopedSections?.length ?? 0) > 0
                          ? ` ${t('copilotAdoption.page.scopeBanner.unscopedSections', {
                              v0: describeUnscopedSections(t, summary.unscopedSections ?? []),
                            })}`
                          : '',
                      link: (
                        <Link onClick={() => setEmailDomain(null)} data-print="hide">
                          {t('copilotAdoption.page.scopeBanner.showAllDomains')}
                        </Link>
                      ),
                    })}
                  </MessageBarBody>
                </MessageBar>
              )}

              {tab === 'executive' &&
                (summary.licensedUsers === 0 && !summary.figuresIncomplete && !summary.scopedEmailDomain ? (
                  // Only a GENUINE zero means "nothing set up yet". When the licence queries timed out the
                  // count also degrades to zero, and showing the first-run screen then tells a tenant with
                  // thousands of seats that it has none at all.
                  //
                  // A domain filter is the same trap one level down: an acquired business using Copilot
                  // Chat with no seats of its own is precisely what this view exists to find, and telling
                  // the reader who just went looking for it that "licence data has not been imported" is
                  // both wrong and the opposite of the finding.
                  <FirstRunState summary={summary} />
                ) : (
                  <ExecutiveTab
                    summary={summary}
                    onDrillToAction={drillToAction}
                    onShowLicensedDetails={showLicensedDetails}
                    onShowOpportunityDetails={showOpportunityDetails}
                    onOpenTab={openTab}
                    selectedEmailDomain={emailDomain}
                    onSelectEmailDomain={setEmailDomain}
                  />
                ))}

              {tab === 'analyst' && (
                <AnalystTab
                  summary={summary}
                  sql={sql}
                  onDrillToAction={drillToAction}
                  onOpenTab={openTab}
                  selectedEmailDomain={emailDomain}
                  onSelectEmailDomain={setEmailDomain}
                />
              )}

              {tab === 'licensed' && (
                <LicensedUsersPanel
                  key={`${drillAction ?? 'all'}::${emailDomain ?? 'all'}`}
                  windowDays={windowDays}
                  filterOptions={filterOptions}
                  actionPlan={summary.actionPlan}
                  options={summary.options}
                  dataSources={summary.dataSources}
                  initialAction={drillAction}
                  emailDomain={emailDomain}
                />
              )}

              {tab === 'unlicensed' && (
                <UnlicensedPanel
                  unlicensed={summary.unlicensed}
                  options={summary.options}
                  windowDays={windowDays}
                  sql={sql}
                />
              )}

              {tab === 'agents' && (
                <AgentsPanel
                  estate={summary.agents}
                  agents={summary.agents.agents}
                  options={summary.options}
                  windowDays={windowDays}
                  sql={sql}
                />
              )}

              {tab === 'cowork' && (
                <CoworkPanel
                  key={emailDomain ?? 'all'}
                  windowDays={windowDays}
                  summary={summary}
                  filterOptions={filterOptions}
                  options={summary.options}
                  emailDomain={emailDomain}
                />
              )}

              {tab === 'opportunities' && (
                <OpportunitiesPanel
                  key={emailDomain ?? 'all'}
                  windowDays={windowDays}
                  summary={summary}
                  filterOptions={filterOptions}
                  options={summary.options}
                  guidanceLinks={summary.guidanceLinks}
                  emailDomain={emailDomain}
                />
              )}

              {tab === 'method' && <MethodTab summary={summary} />}
            </div>
          )}
        </>
      )}
    </div>
  );
}

/**
 * What the overview shows when there is nothing to show.
 *
 * The undesigned version of this state is seventeen cards each saying "no data", which reads as a
 * broken page rather than an unfinished import - and gives the reader no idea which of several
 * quite different causes applies. Zero licensed users is nearly always one of three things, so say
 * which three and what to do about each.
 */

function FirstRunState({ summary }: { summary: CopilotAdoptionSummary }) {
  const styles = useStyles();
  const t = useT();
  const tNode = useTNode();

  return (
    <Card>
      <div className={styles.cardHead}>
        <div>
          <Text weight="semibold" size={500}>{t('copilotAdoption.noLicences.title')}</Text>
          <Text size={300} block className={styles.muted}>{t('copilotAdoption.noLicences.completedEmpty')}</Text>
        </div>
      </div>
      <div className={styles.cardBody}>
        <Text size={300} block style={{ marginBottom: '10px' }}>{t('copilotAdoption.noLicences.reasonIntro')}</Text>
        <ol style={{ margin: 0, paddingInlineStart: '20px', lineHeight: 1.7 }}>
          <li>
            {tNode('copilotAdoption.noLicences.reason.importPending.body', {
              heading: <strong>{t('copilotAdoption.noLicences.reason.importPending.title')}</strong>,
            })}
          </li>
          <li>
            {tNode('copilotAdoption.noLicences.reason.noSkuMarked.body', {
              heading: <strong>{t('copilotAdoption.noLicences.reason.noSkuMarked.title')}</strong>,
            })}
          </li>
          <li>
            {tNode('copilotAdoption.noLicences.reason.noTenantLicences.body', {
              heading: <strong>{t('copilotAdoption.noLicences.reason.noTenantLicences.title')}</strong>,
              tab: <em>{t('copilotAdoption.noLicences.licenceOpportunitiesTab')}</em>,
            })}
          </li>
        </ol>
        {summary.unlicensed?.activeUsers > 0 && (
          <MessageBar intent="info" style={{ marginTop: '14px' }}>
            <MessageBarBody>
              {t('copilotAdoption.noLicences.unlicensedActivityWarning', { v0: formatCount(summary.unlicensed.activeUsers) })}
            </MessageBarBody>
          </MessageBar>
        )}
      </div>
    </Card>
  );
}


/** The executive view: a board-pack summary in three acts, with detail one click away. */
function ExecutiveTab({
  summary,
  onDrillToAction,
  onShowLicensedDetails,
  onShowOpportunityDetails,
  onOpenTab,
  selectedEmailDomain,
  onSelectEmailDomain,
}: {
  summary: CopilotAdoptionSummary;
  onDrillToAction?: (code: string) => void;
  onShowLicensedDetails: () => void;
  onShowOpportunityDetails: () => void;
  onOpenTab?: (tab: AdoptionTab) => void;
  selectedEmailDomain?: string | null;
  onSelectEmailDomain?: (domain: string | null) => void;
}) {
  const styles = useStyles();
  const t = useT();
  const o = summary.options;
  const { assumptions: timeSavedAssumptions } = useTimeSavedAssumptions(summary);
  const kpis = buildExecutiveKpis(summary, t, timeSavedAssumptions, onOpenTab);
  return (
    <>
      <KpiGrid items={kpis} />

      <SectionHead
        index={1}
        title={t('copilotAdoption.page.whereWeStand')}
        blurb={t('copilotAdoption.page.executive.whereWeStandBlurb')}
      />

      <div className={styles.twoUp}>
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>{t('copilotAdoption.page.licencePosition')}</Text>
              <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.theBoardPackViewManySeatsUseManyBecome')}</Text>
            </div>
            <InfoTip
              title={t('copilotAdoption.page.licencePosition2')}
              content={{
                what: t('copilotAdoption.page.theHeadlineLicencePositionAssignedSeatsAdoptionHabitReclaim'),
                how: t('copilotAdoption.page.adoptionMeansLeastOneCopilotInteractionDaysHabitMeans', { v0: o.windowDays, v1: o.establishedScore }),
                source:
                  t('copilotAdoption.page.useAnalystViewSqlLicensedUsersLicenceOpportunitiesTabs'),
              }}
            />
          </div>
          <div className={`${styles.cardBody} ${styles.gauges}`}>
            <GaugeRing
              value={summary.adoptionRatePct}
              label={t('copilotAdoption.page.adoptionRate')}
              sublabel={t('copilotAdoption.page.gauge.activeOfLicensed', { active: formatCount(summary.activeUsers), total: formatCount(summary.scoredUsers) })}
            />
            <GaugeRing
              value={summary.habitRatePct}
              label={t('copilotAdoption.page.habitRate')}
              sublabel={t('copilotAdoption.page.gauge.establishedOrChampion', { count: formatCount(summary.habitualUsers) })}
            />
          </div>
          {/* Drill-through into another tab. On paper there is no other tab to drill into. */}
          <div style={{ display: 'flex', gap: '8px', flexWrap: 'wrap', marginTop: '12px' }} data-print="hide">
            <Button appearance="secondary" onClick={onShowLicensedDetails}>{t('copilotAdoption.page.reviewLicensedUsers')}</Button>
            {summary.recommendedForLicence > 0 && (
              <Button appearance="secondary" onClick={onShowOpportunityDetails}>{t('copilotAdoption.page.reviewLicenceCandidates')}</Button>
            )}
          </div>
        </Card>

        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>{t('copilotAdoption.page.reclaimConfidenceSplit')}</Text>
              <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.actionableReclaimDeliberatelyNarrowerAllIdleSeatsDisabledAccounts')}</Text>
            </div>
            <InfoTip
              title={t('copilotAdoption.page.reclaimConfidenceSplit2')}
              content={{
                what: t('copilotAdoption.page.idleDisabledCopilotSeatsSplitSafelyReclaimed'),
                how: t('copilotAdoption.page.certainDisabledAccountsProbableEnabledLongTenuredNeverUsed', { v0: formatCount(summary.reclaimReviewSeats), v1: formatCount(summary.reclaimExcludedUsers) }),
                source:
                  t('copilotAdoption.page.theSameReclaimTiersAvailableLicensedUsersTabAdministrators'),
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <CategoryBarChart
              valueLabel={t('copilotAdoption.page.seats')}
              categories={[
                { label: t('copilotAdoption.page.certain'), value: summary.reclaimCertainSeats },
                { label: t('copilotAdoption.page.probable'), value: summary.reclaimProbableSeats },
                { label: t('copilotAdoption.page.review'), value: summary.reclaimReviewSeats },
                { label: t('copilotAdoption.page.excluded'), value: summary.reclaimExcludedUsers },
              ].filter((c) => c.value > 0)}
            />
            {summary.reclaimableSeats === 0 && (
              <Text className={styles.muted}>{t('copilotAdoption.page.noSeatsCurrentlyMeetCertainProbableReclaimRules')}</Text>
            )}
          </div>
        </Card>
      </div>

      <SectionHead
        index={2}
        title={t('copilotAdoption.page.whereWorkingFailing')}
        blurb={t('copilotAdoption.page.executive.workingFailingBlurb')}
      />

      <div className={styles.twoUp}>
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>{t('copilotAdoption.page.departmentLeagueTable')}</Text>
              <Text size={200} block className={styles.muted}>
                {t('copilotAdoption.page.lowestHabitRateDepartmentsCappedTopDepartmentsAboveSeat', { v0: o.topSegments })}
              </Text>
            </div>
            <InfoTip
              title={t('copilotAdoption.page.departmentLeagueTable2')}
              content={{
                what: t('copilotAdoption.page.departmentsRankedShareLicensedUsersFormedCopilotHabit'),
                how: t('copilotAdoption.page.habitualUsersEstablishedChampionDepartmentsLicencesOmittedExecutiveTable', { v0: o.minSeatsPerSegment, v1: o.topSegments }),
                source:
                  t('copilotAdoption.page.theAnalystViewKeepsFullAdoptionOpportunityBreakdownsDetail'),
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <ExecutiveDepartmentTable summary={summary} />
          </div>
        </Card>

        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>{t('copilotAdoption.page.adoptionFunnel')}</Text>
              <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.everyStageSubsetOneAboveLargestDropExecutiveDiagnosis')}</Text>
            </div>
            <InfoTip
              title={t('copilotAdoption.page.adoptionFunnel2')}
              content={{
                what: t('copilotAdoption.page.theLicensedPopulationNarrowedOneStageTimeSingleBiggest'),
                how: t('copilotAdoption.page.licensedCopilotSeatHoldersEverUsedIncludesActiveDormant', { v0: o.windowDays, v1: o.establishedScore, v2: o.championScore }),
                source: t('copilotAdoption.page.theSameFunnelAppearsAnalystViewSqlUsedReproduce'),
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <AdoptionFunnel stages={summary.funnel} options={o} />
          </div>
        </Card>
      </div>

      {/* Only shown on a multi-domain tenant. On a single-domain one it is a table comparing an
          organisation with itself, which is noise on the board-pack view.

          Read defensively because during a rolling deploy the SPA can be newer than the API that
          answers it, and a missing array would take the whole page down rather than hide one card. */}
      {(summary.emailDomains?.length ?? 0) > 1 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>{t('copilotAdoption.page.adoptionEmailDomain')}</Text>
              <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.theOrganisationsSharingTenantComparedSideSideWorstAdoption')}</Text>
            </div>
            <InfoTip
              title={t('copilotAdoption.page.adoptionEmailDomain2')}
              content={{
                what: t('copilotAdoption.page.copilotAdoptionEachEmailDomainTenantPracticeEachOrganisations'),
                how: t('copilotAdoption.page.theDomainTakenEachPersonSignNameDomainNeeds', { v0: o.minSeatsPerSegment }),
                source:
                  t('copilotAdoption.page.worthReadingAlongsideDepartmentTableInsteadTenantBuiltAcquisition'),
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <EmailDomainPanel
              summary={summary}
              selectedDomain={selectedEmailDomain}
              onSelectDomain={onSelectEmailDomain}
            />
          </div>
        </Card>
      )}

      <SectionHead
        index={3}
        title={t('copilotAdoption.page.whatWeDoingAbout')}
        blurb={t('copilotAdoption.page.executive.enablementBlurb')}
      />

      <Card>
        <div className={styles.cardHead}>
          <div>
            <Text weight="semibold" size={400}>{t('copilotAdoption.page.enablementPlan')}</Text>
            <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.everyLicensedUserNeedsExactlyOneNextStepClick')}</Text>
          </div>
          <InfoTip
            title={t('copilotAdoption.page.enablementPlan2')}
            content={{
              what: t('copilotAdoption.page.thePerUserRecommendedActionsAggregatedWorkProgrammeRun'),
              how: t('copilotAdoption.page.derivedEngagementBandMiddleBandsBreadthCopilotUseClick'),
              source: t('copilotAdoption.page.countsSumLicensedUsersAnalysisScoredOwnerOutcomeFields', { v0: formatCount(summary.scoredUsers) }),
            }}
          />
        </div>
        <div className={styles.cardBody}>
          <ActionPlan actions={summary.actionPlan} options={o} onSelect={onDrillToAction} />
        </div>
      </Card>
    </>
  );
}

function ExecutiveDepartmentTable({ summary }: { summary: CopilotAdoptionSummary }) {
  const styles = useStyles();
  const t = useT();
  // Both collections are absent when the analysis returned early (a failed licence-types query
  // marks the summary incomplete without populating the segment breakdowns), so neither can be
  // spread or mapped unguarded.
  const candidatesByDepartment = new Map((summary.opportunityByDepartment ?? []).map((r) => [r.label, r.value]));
  const rows = [...(summary.habitByDepartment ?? [])]
    .map((row) => {
      const habitRatePct = row.licensedUsers > 0 ? (row.habitualUsers / row.licensedUsers) * 100 : 0;
      const idleSeats = row.neverUsedUsers;
      const candidates = candidatesByDepartment.get(row.segment) ?? 0;
      return { ...row, habitRatePct, idleSeats, candidates, netOpportunity: candidates - idleSeats };
    })
    .sort((a, b) => a.habitRatePct - b.habitRatePct || b.licensedUsers - a.licensedUsers)
    .slice(0, 8);

  if (rows.length === 0) {
    return (
      <Text className={styles.muted}>{t('copilotAdoption.page.noDepartmentEnoughCopilotLicencesProduceMeaningfulExecutiveLeague')}</Text>
    );
  }

  return (
    <table className={styles.skuTable} aria-label={t('copilotAdoption.page.departmentLeagueTable3')}>
      <thead>
        <tr>
          <th className={styles.skuCell}>{t('copilotAdoption.page.department')}</th>
          <th className={styles.skuCell}>{t('copilotAdoption.page.habitRate2')}</th>
          <th className={styles.skuCell}>{t('copilotAdoption.page.licences')}</th>
          <th className={styles.skuCell}>{t('copilotAdoption.page.neverUsed')}</th>
          <th className={styles.skuCell}>{t('copilotAdoption.page.candidates')}</th>
          <th className={styles.skuCell}>{t('copilotAdoption.page.reassignmentSignal')}</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((row) => (
          <tr key={row.segment}>
            <td className={styles.skuCell}>{row.segment}</td>
            <td className={styles.skuCell}>{formatPct(row.habitRatePct)}</td>
            <td className={styles.skuCell}>{formatCount(row.licensedUsers)}</td>
            <td className={styles.skuCell}>{formatCount(row.idleSeats)}</td>
            <td className={styles.skuCell}>{formatCount(row.candidates)}</td>
            <td className={styles.skuCell}>
              {row.netOpportunity > 0
                ? t('copilotAdoption.page.moreCandidatesNeverUsedSeats', { v0: formatCount(row.netOpportunity) })
                : row.netOpportunity < 0
                  ? t('copilotAdoption.page.moreNeverUsedSeatsCandidates', { v0: formatCount(Math.abs(row.netOpportunity)) })
                  : t('copilotAdoption.page.balanced')}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/** The analyst view: every diagnostic chart and the SQL popovers admins use to verify them. */
function AnalystTab({
  summary,
  sql,
  onDrillToAction,
  onOpenTab,
  selectedEmailDomain,
  onSelectEmailDomain,
}: {
  summary: CopilotAdoptionSummary;
  sql: Record<string, string> | null;
  onDrillToAction?: (code: string) => void;
  onOpenTab?: (tab: AdoptionTab) => void;
  selectedEmailDomain?: string | null;
  onSelectEmailDomain?: (domain: string | null) => void;
}) {
  const styles = useStyles();
  const t = useT();
  const { assumptions: timeSavedAssumptions } = useTimeSavedAssumptions(summary);
  const kpis = buildKpis(summary, t, timeSavedAssumptions, onOpenTab);
  const o = summary.options;
  const accountabilityDimensionLabel = summary.accountabilityDimensionLabel ?? 'Direct manager';
  const accountabilityDimensionDescription = accountabilityDimensionLabel.toLowerCase();

  // The band slices and the action plan are built from the users actually scored, which is capped by
  // MaxLicensedUsersScored. That cap is far above any real Copilot deployment and raises an explicit
  // warning when it bites - but the donut must still add up to what it is drawing, so the centre is
  // the sum of the slices rather than the licence count.
  const analysedUsers = summary.scoredUsers;
  // The scored population and the seat count are identical on all but the very largest tenants.
  // Only introduce the word "analysed" when they actually differ - otherwise it is an internal row
  // cap leaking into the UI as a business term, and it makes a simple number look qualified.
  const capped = summary.scoredUsers > 0 && summary.scoredUsers < summary.licensedUsers;
  const populationWordKey = capped ? 'copilotAdoption.page.population.analysed' : 'copilotAdoption.page.population.licensed';
  const bandThresholds = {
    champion: o.championScore,
    established: o.establishedScore,
    developing: o.developingScore,
  };

  // Readiness for Cowork, from the readiness assessment rather than from Cowork usage. This is the
  // gauge that must survive: the Cowork ADOPTION percentage below is suppressed whenever the Cowork
  // spending-policy scope is unknown - which is the normal case, because nothing in the import can
  // see that scope - so gating the whole Cowork slot on it removed Cowork from this card entirely on
  // most tenants. Readiness has no such denominator: it is a share of the seat holders this tool
  // actually scored, so it is always answerable when the readiness step ran.
  const coworkReadinessPct =
    summary.coworkReadinessAvailable && summary.coworkScoredUsers > 0
      ? (summary.coworkRecommendedForPolicy / summary.coworkScoredUsers) * 100
      : null;

  return (
    <>
      <KpiGrid items={kpis} />


      <SectionHead
        index={1}
        title={t('copilotAdoption.page.whereStand')}
        blurb={t('copilotAdoption.page.analyst.whereStandBlurb')}
      />

      <Card>
        <div className={styles.cardHead}>
          <div>
            <Text weight="semibold" size={400}>{t('copilotAdoption.page.whereStand2')}</Text>
            <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.theRatesDecideWhetherLicencesEarningKeepAgainstScale')}</Text>
          </div>
          <InfoTip
            title={t('copilotAdoption.page.whereStand3')}
            content={{
              what: t('copilotAdoption.page.adoptionRateShareLicensedUsersTouchedCopilotAllHabit'),
              how: t('copilotAdoption.page.theColouredArcJudgementScaleSmoothGradientContinuousRamp', { v0: describeBands(), v1: o.establishedScore, v2: o.coworkLoadMinScore, v3: o.coworkFluencyMinScore }),
              source:
                t('copilotAdoption.page.theGapBetweenAdoptionHabitGaugesFindingAdoptionHabit'),
            }}
          />
        </div>
        <div className={`${styles.cardBody} ${styles.gauges}`}>
          <GaugeRing
            value={summary.adoptionRatePct}
            label={t('copilotAdoption.page.adoptionRate2')}
            sublabel={t('copilotAdoption.page.gauge.usersTouchedCopilot', { active: formatCount(summary.activeUsers), total: formatCount(summary.scoredUsers), population: t(populationWordKey) })}
          />
          <GaugeRing
            value={summary.habitRatePct}
            label={t('copilotAdoption.page.habitRate3')}
            sublabel={t('copilotAdoption.page.gauge.madePartWorkingWeek', { count: formatCount(summary.habitualUsers) })}
          />
          {summary.coworkDetected && summary.coworkAdoptionPct !== null && (
            <GaugeRing
              value={summary.coworkAdoptionPct}
              label={t('copilotAdoption.page.coworkAdoption')}
              sublabel={t('copilotAdoption.page.gauge.eligibleUsedCowork', { count: formatCount(summary.coworkUsers) })}
            />
          )}
          {coworkReadinessPct !== null && (
            <GaugeRing
              value={coworkReadinessPct}
              label={t('copilotAdoption.page.readinessCowork')}
              sublabel={t('copilotAdoption.page.gauge.readyForCoworkScope', { ready: formatCount(summary.coworkRecommendedForPolicy), total: formatCount(summary.coworkScoredUsers) })}
            />
          )}
        </div>
      </Card>

      <Card>
        <div className={styles.cardHead}>
          <div>
            <Text weight="semibold" size={400}>{t('copilotAdoption.page.adoptionFunnel3')}</Text>
            <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.everyStageSubsetOneAboveBiggestDropEffortShould')}</Text>
          </div>
          <div className={styles.cardTools}>
            <InfoTip
              title={t('copilotAdoption.page.adoptionFunnel4')}
              content={{
                what: t('copilotAdoption.page.theLicensedPopulationNarrowedOneStageTimeSingleBiggest2'),
                how: t('copilotAdoption.page.licensedHoldersCopilotLicenceSkuEverUsedCountedActive', { v0: o.establishedScore, v1: o.championScore }),
                source:
                  t('copilotAdoption.page.licensedCountsComeImportedLicenceAssignmentsEveryActivityStage'),
              }}
            />
            {sql?.licensedUsers && <SqlPopover sql={sql.licensedUsers} title={t('copilotAdoption.page.sqlBehindTheseFigures')} />}
          </div>
        </div>
        <div className={styles.cardBody}>
          <AdoptionFunnel stages={summary.funnel} options={o} />
        </div>
      </Card>

      <SectionHead
        index={2}
        title={t('copilotAdoption.page.whatNext')}
        blurb={t('copilotAdoption.page.analyst.whatNextBlurb')}
      />

      <Card>
        <div className={styles.cardHead}>
          <div>
            <Text weight="semibold" size={400}>{t('copilotAdoption.page.departmentLeagueTable4')}</Text>
            <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.theExecutiveDepartmentSummaryRepeatedHereHabitRateUnused')}</Text>
          </div>
          <InfoTip
            title={t('copilotAdoption.page.departmentLeagueTable5')}
            content={{
              what: t('copilotAdoption.page.departmentsRankedHabitRateReassignmentSignalShownCountsColour'),
              how: t('copilotAdoption.page.habitRateHabitualUsersDividedLicensedUsersNeverUsed'),
              source: t('copilotAdoption.page.thisRepeatsExecutiveViewFigureNoExecutiveOnlyNumber'),
            }}
          />
        </div>
        <div className={styles.cardBody}>
          <ExecutiveDepartmentTable summary={summary} />
        </div>
      </Card>

      <Card>
        <div className={styles.cardHead}>
          <div>
            <Text weight="semibold" size={400}>{t('copilotAdoption.page.enablementPlan3')}</Text>
            <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.everyLicensedUserNeedsExactlyOneTheseNextSteps')}</Text>
          </div>
          <InfoTip
            title={t('copilotAdoption.page.enablementPlan4')}
            content={{
              what: t('copilotAdoption.page.thePerUserRecommendedActionsAggregatedEachActionStated'),
              how: t('copilotAdoption.page.derivedEngagementBandMiddleBandsBreadthScoreWellUser'),
              source: t('copilotAdoption.page.orderedSizeEveryUserGetsExactlyOneActionCounts', { v0: formatCount(
                analysedUsers,
              ) }),
            }}
          />
        </div>
        <div className={styles.cardBody}>
          <ActionPlan actions={summary.actionPlan} options={o} onSelect={onDrillToAction} />
        </div>
      </Card>

      <Card>
        <div className={styles.cardHead}>
          <div>
            <Text weight="semibold" size={400}>{t('copilotAdoption.page.accountabilityRollUp')}</Text>
            <Text size={200} block className={styles.muted}>
              {t('copilotAdoption.page.aggregateOnlyViewSortedLargestAbsoluteOpportunityFirstGroups', {
                v0: accountabilityDimensionDescription,
                v1: o.minSeatsPerSegment,
              })}
            </Text>
          </div>
          <InfoTip
            title={t('copilotAdoption.page.accountabilityRollUp2')}
            content={{
              what: t('copilotAdoption.page.leaderSafeAggregateViewSeatsAdoptionHabitReclaimTiers', { v0: accountabilityDimensionDescription }),
              how: t('copilotAdoption.page.theDimensionDefaultsDirectManagerUsersManagerGroupedExplicitly', { v0: o.minSeatsPerSegment }),
              source:
                t('copilotAdoption.page.thisDeliberatelyAddNamedPerUserLeaderViewDrill'),
            }}
          />
        </div>
        <div className={styles.cardBody}>
          <AccountabilityRollupTable
            rows={summary.accountabilityRollup}
            segmentLabel={accountabilityDimensionLabel}
          />
        </div>
      </Card>

      <Card>
        <div className={styles.cardHead}>
          <div>
            <Text weight="semibold" size={400}>{t('copilotAdoption.page.adoptionDepartment')}</Text>
            <Text size={200} block className={styles.muted}>
              {t('copilotAdoption.page.lowestAdoptionFirstRunningOrderEnablementPlanDepartmentsFewer', { v0: o.minSeatsPerSegment })}
            </Text>
          </div>
          <InfoTip
            title={t('copilotAdoption.page.adoptionDepartment2')}
            content={{
              what: t('copilotAdoption.page.copilotAdoptionEachDepartmentWorstFirstRawLicenceCounts'),
              how: t('copilotAdoption.page.departmentComesImportedUserMetadataUsersNoneGroupedNo', { v0: o.minSeatsPerSegment }),
              source:
                t('copilotAdoption.page.theCountsShownNextRateDeliberatelyAcrossSixLicences'),
            }}
          />
        </div>
        <div className={styles.cardBody}>
          <SegmentTable rows={summary.adoptionByDepartment} segmentLabel={t('copilotAdoption.page.department')} bands={bandThresholds} />
        </div>
      </Card>

      {(summary.emailDomains?.length ?? 0) > 1 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>{t('copilotAdoption.page.adoptionEmailDomain3')}</Text>
              <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.eachRowOneOrganisationsSharingTenantIdleSeatsNext')}</Text>
            </div>
            <InfoTip
              title={t('copilotAdoption.page.adoptionEmailDomain4')}
              content={{
                what: t('copilotAdoption.page.adoptionReclaimUnlicensedDemandLicenceCandidatesEachEmailDomain'),
                how: t('copilotAdoption.page.theDomainTakenEachPersonSignNamePeopleWhose', { v0: o.minSeatsPerSegment }),
                source:
                  t('copilotAdoption.page.invitedGuestsAttributedOwnHomeOrganisationTenantUsingDomain'),
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <EmailDomainPanel
              summary={summary}
              selectedDomain={selectedEmailDomain}
              onSelectDomain={onSelectEmailDomain}
            />
          </div>
        </Card>
      )}

      {summary.opportunityByDepartment.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>{t('copilotAdoption.page.whereUnmetDemand')}</Text>
              <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.departmentsMostRecommendedLicenceCandidatesPairDepartmentAdoptionTable')}</Text>
            </div>
            <div className={styles.cardTools}>
              <InfoTip
                title={t('copilotAdoption.page.whereUnmetDemand2')}
                content={{
                  what: t('copilotAdoption.page.howManyUnlicensedUsersEachDepartmentRecommendedLicenceEither', { v0: o.opportunityProvenDemandMinActiveDays, v1: o.opportunityRecommendScore }),
                  how: t('copilotAdoption.page.onlyRecommendedCandidatesCountedEveryUnlicensedUserDisabledAccounts'),
                  source:
                    t('copilotAdoption.page.readAgainstDepartmentAdoptionTableDepartmentAppearsBothLicences'),
                }}
              />
              {sql?.licenceOpportunities && (
                <SqlPopover sql={sql.licenceOpportunities} title={t('copilotAdoption.page.sqlBehindChart')} />
              )}
            </div>
          </div>
          <div className={styles.cardBody}>
            <CategoryBarChart categories={summary.opportunityByDepartment} valueLabel={t('copilotAdoption.page.candidates2')} />
          </div>
        </Card>
      )}

      <SectionHead
        index={3}
        title={t('copilotAdoption.page.howCopilotBeingUsed')}
        blurb={t('copilotAdoption.page.analyst.evidenceBlurb')}
      />

      <Card>
        <div className={styles.cardHead}>
          <div>
            <Text weight="semibold" size={400}>{t('copilotAdoption.page.howOftenPeopleOpenCopilot')}</Text>
            <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.ofLicensedUsersUsedCopilotAllManyDaysMonth')}</Text>
          </div>
          <InfoTip
            title={t('copilotAdoption.page.howOftenPeopleOpenCopilot2')}
            content={{
              what: t('copilotAdoption.page.activeLicensedUsersSplitOftenUseCopilotNoWeighting'),
              how: t('copilotAdoption.page.activeDaysSelectedPeriodRestatedDaysPerDayMonth', { v0: o.habitBucketNormalisationDays, v1: o.habitModerateMinDays - 1, v2: o.habitModerateMinDays, v3: o.habitFrequentMinDays - 1, v4: o.habitFrequentMinDays, v5: o.habitDailyMinDays - 1, v6: o.habitDailyMinDays }),
              formula: t('copilotAdoption.page.dayspermonthRoundActivedays', { v0: o.habitBucketNormalisationDays, v1: o.windowDays }),
              source:
                t('copilotAdoption.page.thisDeliberatelySameMeasureHabitRateTopPageOne'),
            }}
          />
        </div>
        <div className={styles.cardBody}>
          <HabitStrip buckets={summary.habitBuckets} options={o} />
        </div>
      </Card>

      <div className={styles.twoUp}>
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>{t('copilotAdoption.page.engagementMix')}</Text>
              <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.everyLicensedUserExactlyOneBandNeverUsedDormant')}</Text>
            </div>
            <InfoTip
              title={t('copilotAdoption.page.engagementMix2')}
              content={{
                what: t('copilotAdoption.page.theWholeLicensedPopulationSplitSixMutuallyExclusiveEngagement'),
                how: t('copilotAdoption.page.championEstablishedDevelopingTriallingAnyoneNoActivityPeriodScored', { v0: o.championScore, v1: o.establishedScore, v2: o.developingScore, v3: o.historyDays }),
                source:
                  t('copilotAdoption.page.theTwoZeroActivityBandsSeparatedNeedOppositeResponses'),
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <DonutChart
              categories={summary.bandBreakdown.map((b) => ({ ...b, label: adoptionBandLabel(t, b.label, b.label) }))}
              colours={BAND_COLOUR_LIST}
              centreValue={formatCount(analysedUsers)}
              centreLabel={t('copilotAdoption.page.licensedUsersCentreLabel')}
            />
          </div>
        </Card>

        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>{t('copilotAdoption.page.whereCopilotUsed')}</Text>
              <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.interactionsAppAcrossLicensedUsersOftenFastestWaySpot')}</Text>
            </div>
            <div className={styles.cardTools}>
              <InfoTip
                title={t('copilotAdoption.page.whereCopilotUsed2')}
                content={{
                  what: t('copilotAdoption.page.totalCopilotInteractionsPeriodAppHappenedSizedArea'),
                  how: t('copilotAdoption.page.everyInteractionCopilotAuditLogLicensedUserAttributedApp', { v0: o.topSegments }),
                  source:
                    t('copilotAdoption.page.needsCopilotAuditImportMicrosoftOwnUsageReportBreak'),
                }}
              />
              {sql?.usageByApp && <SqlPopover sql={sql.usageByApp} title={t('copilotAdoption.page.sqlBehindChart2')} />}
            </div>
          </div>
          <div className={styles.cardBody}>
            {summary.usageByApp.length > 0 ? (
              <TreemapChart categories={summary.usageByApp} valueLabel={t('copilotAdoption.page.interactions')} />
            ) : (
              <Text className={styles.muted}>{t('copilotAdoption.page.noPerAppBreakdownAvailableNeedsCopilotAuditImport')}</Text>
            )}
          </div>
        </Card>
      </div>

      {summary.scoreProfiles.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>{t('copilotAdoption.page.theShapeAdoption')}</Text>
              <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.whereTypicalUserDiffersBestOnesThereforeWhatEnablement')}</Text>
            </div>
            <InfoTip
              title={t('copilotAdoption.page.theShapeAdoption2')}
              content={{
                what: t('copilotAdoption.page.theThreeComponentsEngagementScoreAveragedTypicalActiveUser'),
                how: t('copilotAdoption.page.averagedOverActiveUsersOnlyIdleLicenceScoresZero'),
                source:
                  t('copilotAdoption.page.theGapBetweenTwoOutlinesFindingSizeIfAverage'),
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <RadarChart
              axes={['Frequency', 'Depth', 'Breadth']}
              series={summary.scoreProfiles.map((p, i) => ({
                name: `${scoreProfileLabel(t, p.label)} (${formatCount(p.users)})`,
                colour: i === 0 ? '#0f6cbd' : '#107c10',
                values: [p.frequencyScore, p.depthScore, p.breadthScore],
              }))}
            />
          </div>
        </Card>
      )}

      {summary.intensityByDepartment.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>{t('copilotAdoption.page.usageFrequencyIntensity')}</Text>
              <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.twoDepartmentsSameAdoptionRateSitOppositeCornersChart')}</Text>
            </div>
            <InfoTip
              title={t('copilotAdoption.page.usageFrequencyIntensity2')}
              content={{
                what: t('copilotAdoption.page.eachDepartmentPlottedOftenUsersOpenCopilotHorizontalAgainst'),
                how: t('copilotAdoption.page.onlyUsersWereActiveLeastOnceAveragedUnusedLicences', { v0: o.habitBucketNormalisationDays, v1: o.minSeatsPerSegment }),
                formula:
                  t('copilotAdoption.page.intensityFormula', { normalisationDays: o.habitBucketNormalisationDays, windowDays: o.windowDays }),
                source:
                  t('copilotAdoption.page.bottomRightFrequentButShallowThoseUsersNeedRicher'),
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <IntensityScatter points={summary.intensityByDepartment} options={o} />
          </div>
        </Card>
      )}

      {summary.concentration.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>{t('copilotAdoption.page.howConcentratedUsage')}</Text>
              <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.shareAllCopilotActivityCohortActiveLicensedUsersHeaviest')}</Text>
            </div>
            <InfoTip
              title={t('copilotAdoption.page.howConcentratedUsage2')}
              content={{
                what: t('copilotAdoption.page.activeLicensedUsersRankedInteractionCountCutCohortsShowing'),
                how: t('copilotAdoption.page.onlyUsersWereActiveLeastOnceRankedIncludingIdle'),
                source:
                  t('copilotAdoption.page.thisFigureAdoptionPercentageHidesAdoptionSpreadEvenlyAdoption'),
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <ConcentrationBar bands={summary.concentration} />
          </div>
        </Card>
      )}

      {summary.weeklyVolumeTrend.length > 1 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>{t('copilotAdoption.page.whoDoingCopilotWork')}</Text>
              <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.theSameWeeklyVolumeCompositionComparisonTotalHeightAll')}</Text>
            </div>
            <InfoTip
              title={t('copilotAdoption.page.whoDoingCopilotWork2')}
              content={{
                what: t('copilotAdoption.page.weeklyCopilotInteractionsStackedTotalMakeUpReadableOnce'),
                how: t('copilotAdoption.page.drawnSameSeriesVolumeChartAboveButHiddenWhen'),
                source:
                  t('copilotAdoption.page.worthStatingTradeOffOnlyBottomBandSitsFlat'),
              }}
            />
          </div>
          <div className={styles.cardBody}>
            {hasTrendGaps(summary.weeklyVolumeTrend) ? (
              <MessageBar intent="warning">
                <MessageBarBody>{t('copilotAdoption.page.compositionHiddenLeastOneCompletedWeekUnverifiableImportCoverage')}</MessageBarBody>
              </MessageBar>
            ) : (
              <StackedAreaChart series={summary.weeklyVolumeTrend} valueLabel={t('copilotAdoption.page.interactions')} />
            )}
          </div>
        </Card>
      )}

      {summary.topResourceTypes.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>{t('copilotAdoption.page.whatCopilotReferenced')}</Text>
              <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.howMicrosoftAuditLogTypedResourcesBehindCopilotAnswers')}</Text>
            </div>
            <div className={styles.cardTools}>
              <InfoTip
                title={t('copilotAdoption.page.whatCopilotReferenced2')}
                content={{
                  what: t('copilotAdoption.page.theRawAccessedresourcesTypeValuesRecordedAgainstEachCopilot', { v0: o.topSegments }),
                  how: t('copilotAdoption.page.readGroupsSeparatelyOneRankingOnlyTenantContentAnswers'),
                  source:
                    t('copilotAdoption.page.microsoftPublishesNoListPossibleValuesFieldPurviewDocumentation'),
                }}
              />
              {sql?.resourceTypes && <SqlPopover sql={sql.resourceTypes} title={t('copilotAdoption.page.sqlBehindChart3')} />}
            </div>
          </div>
          <div className={styles.cardBody}>
            <ResourceTypesPanel rows={summary.topResourceTypes} />
          </div>
        </Card>
      )}

      <SectionHead
        index={4}
        title={t('copilotAdoption.page.trendWiderReach')}
        blurb={t('copilotAdoption.page.analyst.trendBlurb')}
      />

      {summary.weeklyTrend.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>{t('copilotAdoption.page.weeklyActiveLicensedUsers')}</Text>
              <Text size={200} block className={styles.muted}>
                {t('copilotAdoption.page.singleAdoptionRateCannotShowWhetherEnablementProgrammeWorking', {
                  v0: summary.coworkDetected ? ` ${t('copilotAdoption.page.theSecondLineTracksMicrosoftCopilotCoworkAdoption')}` : '',
                })}
              </Text>
            </div>
            <div className={styles.cardTools}>
              <InfoTip
                title={t('copilotAdoption.page.weeklyActiveLicensedUsers2')}
                content={{
                  what: t('copilotAdoption.page.distinctLicensedUsersLeastOneCopilotInteractionEachCalendar'),
                  how: t('copilotAdoption.page.weeksStartMondayCountedUtcCurrentPartialWeekExcluded'),
                  source:
                    t('copilotAdoption.page.sixMonthsCompletedHistoryRegardlessPeriodSelectedAboveTrend'),
                }}
              />
              {sql?.weeklyTrend && <SqlPopover sql={sql.weeklyTrend} title={t('copilotAdoption.page.sqlBehindChart4')} />}
            </div>
          </div>
          <div className={styles.cardBody}>
            <TimeSeriesChart series={summary.weeklyTrend} valueLabel={t('copilotAdoption.page.users')} gapNote={t('copilotAdoption.page.trendGapNote')} />
          </div>
        </Card>
      )}

      {summary.weeklyVolumeTrend.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>{t('copilotAdoption.page.weeklyCopilotVolume')}</Text>
              <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.interactionsPeopleLicensedAgainstUnlicensedHeadcountFlattenWhileVolume')}</Text>
            </div>
            <InfoTip
              title={t('copilotAdoption.page.weeklyCopilotVolume2')}
              content={{
                what: t('copilotAdoption.page.totalCopilotInteractionsEachWeekSplitWhetherPersonHolds'),
                how: t('copilotAdoption.page.countsInteractionsPeopleCurrentPartialWeekExcludedUnverifiableAudit'),
                source:
                  t('copilotAdoption.page.bothSeriesComeOnePassOverCopilotAuditLog'),
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <TimeSeriesChart series={summary.weeklyVolumeTrend} valueLabel={t('copilotAdoption.page.interactions2')} gapNote={t('copilotAdoption.page.trendGapNote')} />
          </div>
        </Card>
      )}

      {summary.combinedByDepartment.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>{t('copilotAdoption.page.licensedUnlicensedSideSide')}</Text>
              <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.departmentIdleLicencesHeavyUnlicensedUseLicenceAllocationProblem')}</Text>
            </div>
            <InfoTip
              title={t('copilotAdoption.page.licensedUnlicensedSideSide2')}
              content={{
                what: t('copilotAdoption.page.forEachDepartmentMuchCopilotLicencesUsedMuchCopilot'),
                how: t('copilotAdoption.page.bothInteractionsPerUserColumnsNormalisedDayMonthLicensed', { v0: o.habitBucketNormalisationDays, v1: o.minSeatsPerSegment }),
                source:
                  t('copilotAdoption.page.theShadingMarksOutliersEachColumnLookDepartmentRight'),
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <CombinedSegmentTable rows={summary.combinedByDepartment} />
          </div>
        </Card>
      )}

      {summary.adoptionByCountry.length > 0 && (
        <Card>
          <Text weight="semibold" size={400}>{t('copilotAdoption.page.adoptionCountry')}</Text>
          <Text size={200} block className={styles.muted}>{t('copilotAdoption.page.theSameMeasuresDepartmentTableOrganisationsRunEnablementRegionally')}</Text>
          <div className={styles.cardBody}>
            <SegmentTable rows={summary.adoptionByCountry} segmentLabel={t('copilotAdoption.page.country')} bands={bandThresholds} />
          </div>
        </Card>
      )}
    </>
  );
}

function AccountabilityRollupTable({
  rows,
  segmentLabel,
}: {
  rows: AccountabilityRollupRow[] | null | undefined;
  segmentLabel: string;
}) {
  const styles = useStyles();
  const t = useT();

  // The roll-up is absent whenever the analysis returned early - a failed licence-types query
  // leaves the summary marked incomplete with none of the accountability fields populated - so
  // this cannot assume the server supplied an array.
  if (!rows || rows.length === 0) {
    return <Text className={styles.muted}>{t('copilotAdoption.page.notEnoughLicensedUsersAnyAccountableGroupBreakDown')}</Text>;
  }

  return (
    <table className={styles.skuTable}>
      <thead>
        <tr>
          <th className={styles.skuCell}>{segmentLabel}</th>
          <th className={styles.skuCell}>{t('copilotAdoption.page.seats2')}</th>
          <th className={styles.skuCell}>{t('copilotAdoption.page.adoption')}</th>
          <th className={styles.skuCell}>{t('copilotAdoption.page.habit')}</th>
          <th className={styles.skuCell}>{t('copilotAdoption.page.reclaimTier')}</th>
          <th className={styles.skuCell}>{t('copilotAdoption.page.actionCounts')}</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((row) => (
          <tr key={row.segment}>
            <td className={styles.skuCell}>{row.segment}</td>
            <td className={styles.skuCell}>{formatCount(row.licensedUsers)}</td>
            <td className={styles.skuCell}>
              {t('copilotAdoption.page.active', {
                v0: formatPct(row.adoptionRatePct),
                v1: formatCount(row.activeUsers),
              })}
            </td>
            <td className={styles.skuCell}>
              {t('copilotAdoption.page.habitual', {
                v0: formatPct(row.licensedUsers === 0 ? 0 : (row.habitualUsers / row.licensedUsers) * 100),
                v1: formatCount(row.habitualUsers),
              })}
            </td>
            <td className={styles.skuCell}>
              {t('copilotAdoption.page.reclaimableCertainProbableReview', {
                v0: formatCount(row.reclaimableSeats),
                v1: formatCount(row.reclaimCertainSeats),
                v2: formatCount(row.reclaimProbableSeats),
                v3: formatCount(row.reclaimReviewSeats),
              })}
            </td>
            <td className={styles.skuCell}>
              {t('copilotAdoption.page.needActionReclaimWinBackCoachBroadenDeepenReview', {
                v0: formatCount(row.opportunityUsers),
                v1: formatCount(row.reclaimUsers),
                v2: formatCount(row.reengageUsers),
                v3: formatCount(row.coachUsers),
                v4: formatCount(row.broadenUsers),
                v5: formatCount(row.growUsers),
                v6: formatCount(row.reviewUsers),
              })}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/**
 * The methodology tab.
 *
 * Not optional decoration: the first question asked about any adoption figure is "how did you get
 * that?", and a report used to justify licence spend has to be able to answer it without someone
 * reading the source code.
 */
function MethodTab({ summary }: { summary: CopilotAdoptionSummary }) {
  const styles = useStyles();
  const t = useT();
  const tNode = useTNode();
  const o = summary.options;
  const weights = [o.frequencyWeight, o.depthWeight, o.breadthWeight];
  const weightSum = weights.reduce((total, w) => total + w, 0);
  const frequencyTargetDays = Math.round(o.windowDays * (o.workingDaysPerWeek / 7) * o.frequencyTargetRatio);
  // The EXACT target the scorer divides by (CopilotAdoptionScoring.TargetActiveDays). frequencyTargetDays
  // above is a display label; using it in the arithmetic would print a score the scorer never produces -
  // at a 90-day period the exact target is 38.57 and the label is 39.
  const frequencyTargetExact = Math.max(1, o.windowDays * (o.workingDaysPerWeek / 7) * o.frequencyTargetRatio);
  // Mirrors CopilotAdoptionScoring.OpportunityCopilotTargetForWindow. The Copilot opportunity target is
  // the only one of the four that is a raw total rather than a per-active-day average, so it is scaled
  // from its basis period to the selected window. Quoting the unscaled number here would document a
  // formula that cannot reproduce the scores shown on the Licence opportunities tab.
  // Shown as the computation rather than a rounded product: printing "64.3" at a 90-day period would put
  // the published formula on the wrong side of the recommendation bar for a candidate sitting exactly on
  // it. `approx` is for prose only, never for the formula.
  const opportunityCopilotTargetExpression = `${o.opportunityCopilotTarget} x ${o.windowDays} / ${o.opportunityCopilotTargetBasisDays}`;
  const opportunityCopilotTargetApprox =
    Math.round(
      Math.max(
        1,
        (o.opportunityCopilotTarget * Math.max(1, o.windowDays)) /
          Math.max(1, o.opportunityCopilotTargetBasisDays),
      ) * 10,
    ) / 10;
  // The worked example below has to be computed, not asserted: at a 7-day period half the frequency
  // target is fewer than depthMinActiveDays, so hard-coding full depth would print a score the scorer
  // would never produce - in a panel whose entire purpose is to reproduce the scorer. Active days are a
  // count of distinct calendar dates, so the example uses a whole number.
  const exampleActiveDays = Math.max(1, Math.round(frequencyTargetExact / 2));
  const exampleFrequency = Math.min(1, exampleActiveDays / frequencyTargetExact);
  const exampleDepthConfidence = Math.min(1, exampleActiveDays / Math.max(1, o.depthMinActiveDays));
  const exampleScore = Math.round(
    ((exampleFrequency * o.frequencyWeight +
      1 * exampleDepthConfidence * o.depthWeight +
      (1 / o.breadthTargetApps) * o.breadthWeight) /
      (weightSum || 1)) *
      100,
  );

  return (
    <Card>
      <Accordion multiple collapsible defaultOpenItems={['score']}>
        <AccordionItem value="score">
          <AccordionHeader>{t('copilotAdoption.page.howEngagementScoreCalculated')}</AccordionHeader>
          <AccordionPanel>
            <div className={styles.method}>
              <Text>{t('copilotAdoption.page.eachLicensedUserGetsScoreOutBuiltThreeComponents')}</Text>
              <Text>
                {tNode('copilotAdoption.page.howManyDistinctDaysUsedCopilotAgainstTargetWorking', {
                  heading: <strong>{t('copilotAdoption.page.frequencyScore', { v0: formatPct(weightSharePct(o.frequencyWeight, weights)) })}</strong>,
                  targetRatio: formatPct(o.frequencyTargetRatio * 100),
                  workingDays: o.workingDaysPerWeek,
                  windowDays: o.windowDays,
                  targetDays: <strong>{t('copilotAdoption.page.activeDays', { v0: frequencyTargetDays })}</strong>,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.interactionsPerActiveDayAgainstTargetPerDayWhat', {
                  heading: <strong>{t('copilotAdoption.page.depth', { v0: formatPct(weightSharePct(o.depthWeight, weights)) })}</strong>,
                  target: o.depthTargetInteractionsPerActiveDay,
                  active: <em>{t('copilotAdoption.page.active3')}</em>,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.becauseDepthDividesNumberUserControlsScaledDownActive', {
                  minDays: o.depthMinActiveDays,
                  fewer: <em>{t('copilotAdoption.page.fewer')}</em>,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.howManyDistinctCopilotSurfacesTeamsWordOutlookCopilot', {
                  heading: <strong>{t('copilotAdoption.page.breadth', { v0: formatPct(weightSharePct(o.breadthWeight, weights)) })}</strong>,
                  target: o.breadthTargetApps,
                })}
              </Text>
              <Text>{t('copilotAdoption.page.eachComponentRatioCappedBeforeWeightedNothingAboveTarget')}</Text>
              <div className={styles.formula}>
                {[
                  t('copilotAdoption.page.frequencyMinActivedaysExpectedactivedays'),
                  t('copilotAdoption.page.confidenceMinActivedays', { v0: o.depthMinActiveDays }),
                  t('copilotAdoption.page.depthMinInteractionsActivedaysConfidence', { v0: o.depthTargetInteractionsPerActiveDay }),
                  t('copilotAdoption.page.breadthMinAppsused', { v0: o.breadthTargetApps }),
                  '',
                  `${t('copilotAdoption.page.scoreFrequencyDepthBreadth', {
                    v0: o.frequencyWeight,
                    v1: o.depthWeight,
                    v2: o.breadthWeight,
                  })}\n        / ${weightSum} x 100`,
                ].join('\n')}
              </div>
              <Text>
                {tNode('copilotAdoption.page.overDayPeriodFrequencyTargetActiveDaysUserActive', {
                  heading: <strong>{t('copilotAdoption.page.workedExample')}</strong>,
                  windowDays: o.windowDays,
                  frequencyTargetDays,
                  exampleActiveDays,
                  depthTarget: o.depthTargetInteractionsPerActiveDay,
                  exampleScore,
                  confidence:
                    exampleDepthConfidence < 1
                      ? t('copilotAdoption.page.overShortPeriodActiveDaysNeededFullConfidenceDepth', {
                          v0: exampleActiveDays,
                          v1: o.depthMinActiveDays,
                          v2: formatPct(exampleDepthConfidence * 100),
                        })
                      : t('copilotAdoption.page.activeDaysAboveDepthConfidenceFactorHereChangeNumber', {
                          v0: exampleActiveDays,
                          v1: o.depthMinActiveDays,
                        }),
                })}
              </Text>
            </div>
          </AccordionPanel>
        </AccordionItem>

        <AccordionItem value="bands">
          <AccordionHeader>{t('copilotAdoption.page.whatEngagementBandsHabitBucketsMean')}</AccordionHeader>
          <AccordionPanel>
            <div className={styles.method}>
              <Text>
                {tNode('copilotAdoption.page.turnScoreDecisionChampionAboveEstablishedDevelopingTriallingPoint', {
                  bands: <strong>{t('copilotAdoption.page.bands')}</strong>,
                  championScore: o.championScore,
                  establishedScore: o.establishedScore,
                  developingScore: o.developingScore,
                  habitualUsers: <strong>{t('copilotAdoption.page.establishedAboveWhatHabitualUsersCounts')}</strong>,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.usersActivityPeriodNeverScoredAllScoreZeroPut', {
                  no: <em>{t('copilotAdoption.page.no2')}</em>,
                  dormant: <em>{t('copilotAdoption.page.dormant')}</em>,
                  historyDays: o.historyDays,
                  neverUsed: <em>{t('copilotAdoption.page.neverUsed2')}</em>,
                  idleSeats: <strong>{t('copilotAdoption.page.idleSeats')}</strong>,
                  not: <em>{t('copilotAdoption.page.not')}</em>,
                  reclaimableLicences: <strong>{t('copilotAdoption.page.reclaimableLicences')}</strong>,
                  certain: <em>{t('copilotAdoption.page.certain2')}</em>,
                  probable: <em>{t('copilotAdoption.page.probable2')}</em>,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.answersNarrowerQuestionNoWeightingWhatMakesUsefulSceptical', {
                  heading: <strong>{t('copilotAdoption.page.howOftenPeopleOpenCopilot3')}</strong>,
                  infrequentMax: o.habitModerateMinDays - 1,
                  moderateMin: o.habitModerateMinDays,
                  moderateMax: o.habitFrequentMinDays - 1,
                  frequentMin: o.habitFrequentMinDays,
                  frequentMax: o.habitDailyMinDays - 1,
                  dailyMin: o.habitDailyMinDays,
                })}
              </Text>
              <Text>{t('copilotAdoption.page.becauseReportingPeriodAdjustableActiveDaysRestatedDaysPer', { v0: o.habitBucketNormalisationDays })}</Text>
              <div className={styles.formula}>
                {t('copilotAdoption.page.dayspermonthRoundActivedays2', { v0: o.habitBucketNormalisationDays, v1: o.windowDays })}
              </div>
              <Text>
                {tNode('copilotAdoption.page.theHabitPercentagesShareUsersAllLicencesSomeoneNever', {
                  active: <em>{t('copilotAdoption.page.active3')}</em>,
                })}
              </Text>
            </div>
          </AccordionPanel>
        </AccordionItem>

        <AccordionItem value="actions">
          <AccordionHeader>{t('copilotAdoption.page.howRecommendedActionChosen')}</AccordionHeader>
          <AccordionPanel>
            <div className={styles.method}>
              <Text>{t('copilotAdoption.page.everyLicensedUserGetsExactlyOneRecommendedActionCounts')}</Text>
              <ActionPlan actions={summary.actionPlan} options={o} />
              <Text className={styles.muted}>{t('copilotAdoption.page.onScreenEachUserCarriesTwoWordTagMeaning')}</Text>
            </div>
          </AccordionPanel>
        </AccordionItem>

        <AccordionItem value="opportunity">
          <AccordionHeader>{t('copilotAdoption.page.howLicenceCandidatesRanked')}</AccordionHeader>          <AccordionPanel>
            <div className={styles.method}>
              <Text>
                {tNode('copilotAdoption.page.unlicensedUsersScoredOutFourWeightedSignalsWeightingSet', {
                  provenDemand: <strong>{t('copilotAdoption.page.provenDemand')}</strong>,
                  minDays: o.opportunityProvenDemandMinActiveDays,
                  workloadInferred: <strong>{t('copilotAdoption.page.workloadInferred')}</strong>,
                  score: o.opportunityRecommendScore,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.provenDemandQualifyOwnCopilotSignalWorthScoreBar', {
                  copilotWeight: o.opportunityUnlicensedCopilotWeight,
                  score: o.opportunityRecommendScore,
                  proves: <em>{t('copilotAdoption.page.proves')}</em>,
                  otherWeights:
                    o.opportunityCollaborationWeight + o.opportunityEmailWeight + o.opportunityDocumentWeight,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.theHeaviestSignalWideMarginOnlyOneProvesDemand', {
                  heading: <strong>{t('copilotAdoption.page.alreadyUsingCopilotChatLicencePoints', { v0: o.opportunityUnlicensedCopilotWeight })}</strong>,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.andMakeUpRestIdentifyHeavyKnowledgeWorkersBenefit', {
                  collaboration: <strong>{t('copilotAdoption.page.teamsCollaboration', { v0: o.opportunityCollaborationWeight })}</strong>,
                  email: <strong>{t('copilotAdoption.page.emailVolume', { v0: o.opportunityEmailWeight })}</strong>,
                  documents: <strong>{t('copilotAdoption.page.documentWork', { v0: o.opportunityDocumentWeight })}</strong>,
                })}
              </Text>
              <div className={styles.formula}>
                {[
                  t('copilotAdoption.page.copilotMinUnlicensedcopilotinteractions', { v0: opportunityCopilotTargetExpression }),
                  t('copilotAdoption.page.collabMinTeamsmessagesTeamsmeetings', { v0: o.opportunityCollaborationTarget }),
                  t('copilotAdoption.page.emailMinEmailssentEmailsread', { v0: o.opportunityEmailTarget }),
                  t('copilotAdoption.page.documentsMinFilesviewedoredited', { v0: o.opportunityDocumentTarget }),
                  '',
                  `${t('copilotAdoption.page.scoreCopilotCollab', {
                    v0: o.opportunityUnlicensedCopilotWeight,
                    v1: o.opportunityCollaborationWeight,
                  })} ${t('copilotAdoption.page.emailDocuments', {
                    v0: o.opportunityEmailWeight,
                    v1: o.opportunityDocumentWeight,
                  })}`,
                  '',
                  `${t('copilotAdoption.page.recommendedWhenUnlicensedcopilotactivedays', {
                    v0: o.opportunityProvenDemandMinActiveDays,
                  })} ${t('copilotAdoption.page.provenDemand2')}`,
                  `               ${t('copilotAdoption.page.orScoreWorkloadInferred', { v0: o.opportunityRecommendScore })}`,
                ].join('\n')}
              </div>
              <Text>
                {t('copilotAdoption.page.theCopilotTargetInteractionsPerDaysScaledDayPeriod', {
                  v0: o.opportunityCopilotTarget,
                  v1: o.opportunityCopilotTargetBasisDays,
                  v2: o.windowDays,
                  v3: opportunityCopilotTargetApprox,
                })}
              </Text>
              <Text>
                {t('copilotAdoption.page.eachSignalCappedTargetBeforeWeightingMattersCapSingle', {
                  v0:
                    o.opportunityCollaborationWeight + o.opportunityEmailWeight + o.opportunityDocumentWeight >=
                    o.opportunityRecommendScore
                      ? t('copilotAdoption.page.moreOne')
                      : t('copilotAdoption.page.every'),
                })}
              </Text>
              <Text>{t('copilotAdoption.page.disabledAccountsExcludedCandidateListHoweverKeptLicensedUser')}</Text>
              <Text>
                {tNode('copilotAdoption.page.method.licenceTimeSaved', {
                  heading: <strong>{t('copilotAdoption.page.method.licenceTimeSavedHeading')}</strong>,
                  meetingMinutes: formatNumber(o.copilotMinutesSavedPerMeeting, { maximumFractionDigits: 2 }),
                  mailMinutes: formatNumber(o.copilotMinutesSavedPerMailThread, { maximumFractionDigits: 2 }),
                  documentMinutes: formatNumber(o.copilotMinutesSavedPerDocument, { maximumFractionDigits: 2 }),
                })}
              </Text>
            </div>
          </AccordionPanel>
        </AccordionItem>

        <AccordionItem value="cowork">
          <AccordionHeader>{t('copilotAdoption.page.howCoworkReadinessAssessed')}</AccordionHeader>
          <AccordionPanel>
            <div className={styles.method}>
              <Text>
                {tNode('copilotAdoption.page.itRequiresMicrosoftCopilotLicencePrerequisiteThenBilledUsage', {
                  heading: <strong>{t('copilotAdoption.page.microsoftCopilotCoworkNoLicenceOwn')}</strong>,
                  policy: <em>{t('copilotAdoption.page.spendingPolicy')}</em>,
                })}
              </Text>
              <Text>{t('copilotAdoption.page.coworkAgenticDelegationLayerDescribeOutcomePlansRunsMulti')}</Text>
              <Text>
                {tNode('copilotAdoption.page.isMuchDelegableMultiStepWorkPersonCarriesFour', {
                  heading: <strong>{t('copilotAdoption.page.coordinationLoad')}</strong>,
                  meetingWeight: o.coworkMeetingWeight,
                  meetingTarget: o.coworkMeetingTarget,
                  emailWeight: o.coworkEmailWeight,
                  emailTarget: o.coworkEmailTarget,
                  collaborationWeight: o.coworkCollaborationWeight,
                  collaborationTarget: o.coworkCollaborationTarget,
                  documentWeight: o.coworkDocumentWeight,
                  documentTarget: o.coworkDocumentTarget,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.isEngagementScoreLicensedUsersTabPlusUpPoints', {
                  heading: <strong>{t('copilotAdoption.page.copilotFluency')}</strong>,
                  uplift: o.coworkAgentFamiliarityUplift,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.theBarsCoordinationLoadFluencyMoreSeparateDaysUse', {
                  load: o.coworkLoadMinScore,
                  fluency: o.coworkFluencyMinScore,
                  observed: <strong>{t('copilotAdoption.page.observedCoworkUseTestedFirstWinsOutright')}</strong>,
                  regularDays: o.coworkRegularMinActiveDays,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.establishedTriallingDescribeWhatSomebodyActuallyDonePrimeCandidate', {
                  heading: <strong>{t('copilotAdoption.page.twoSixTiersEvidenceFourPredictions')}</strong>,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.theRecommendedPolicyListPrimeCandidatesEveryoneAlreadyUsing', {
                  plus: <em>{t('copilotAdoption.page.plus')}</em>,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.method.coworkTimeSaved', {
                  heading: <strong>{t('copilotAdoption.page.method.coworkTimeSavedHeading')}</strong>,
                  taskMinutes: formatNumber(o.coworkMinutesSavedPerTask, { maximumFractionDigits: 2 }),
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.microsoftMetersCoworkAgainstSameCreditPoolCopilotStudio', {
                  heading: <strong>{t('copilotAdoption.page.creditFiguresSharedCopilotCreditsPoolCoworkSpend')}</strong>,
                })}
              </Text>
            </div>
          </AccordionPanel>
        </AccordionItem>

        <AccordionItem value="agents">
          <AccordionHeader>{t('copilotAdoption.page.howAgentsUnlicensedUseMeasured')}</AccordionHeader>
          <AccordionPanel>
            <div className={styles.method}>
              <Text>
                {tNode('copilotAdoption.page.anAgentAppearsHereOnlyOnceBeenInvokedCopilot', {
                  heading: <strong>{t('copilotAdoption.page.agents')}</strong>,
                  used: <em>{t('copilotAdoption.page.used')}</em>,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.retireDaysUseReviewBetweenDaysWhileStillCurrent', {
                  heading: <strong>{t('copilotAdoption.page.agentVerdicts')}</strong>,
                  retireDays: o.agentRetireInactiveDays,
                  reviewDays: o.agentReviewInactiveDays,
                  minUsers: o.agentMinUsers,
                  newDays: o.agentNewDays,
                  newLabel: <em>{t('copilotAdoption.page.new')}</em>,
                })}
              </Text>
              <Text>
                {t('copilotAdoption.page.theInventoryDeliberatelyCoversShorterHistoryRestAnalysisDays', {
                  v0: summary.agents.historyDays,
                  v1: o.historyDays,
                  v2: o.agentRetireInactiveDays,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.isReportedPopulationOwnRightUsingIdenticalHabitRules', {
                  heading: <strong>{t('copilotAdoption.page.unlicensedCopilotChat')}</strong>,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.ranksActiveLicensedUsersInteractionCountCutsPercentileCohorts', {
                  heading: <strong>{t('copilotAdoption.page.usageConcentration')}</strong>,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.isDomainEachPersonSignNameLowerCasedTenant', {
                  heading: <strong>{t('copilotAdoption.page.emailDomain')}</strong>,
                  home: <em>{t('copilotAdoption.page.home')}</em>,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.reCalculatesEveryFigurePageDomainPeopleSimplyHide', {
                  heading: <strong>{t('copilotAdoption.page.filteringEmailDomain')}</strong>,
                })}
              </Text>
            </div>
          </AccordionPanel>
        </AccordionItem>

        <AccordionItem value="sources">
          <AccordionHeader>{t('copilotAdoption.page.whereDataComes')}</AccordionHeader>          <AccordionPanel>
            <div className={styles.method}>
              <Text>
                {tNode('copilotAdoption.page.coversEveryUserIncludingUnlicensedCopilotChatUseMatches', {
                  heading: <strong>{t('copilotAdoption.page.copilotAuditLog')}</strong>,
                  status: summary.dataSources.auditAvailable
                    ? t('copilotAdoption.page.available')
                    : t('copilotAdoption.page.noDataPeriod'),
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.microsoftSaysAuditLogAggregatesIntendedMatchOfficialCopilot', {
                  heading: <strong>{t('copilotAdoption.page.whyDiffersMicrosoftReport')}</strong>,
                  link: <Link href={MICROSOFT_COPILOT_USAGE_REPORT_FAQ_URL} target="_blank" rel="noreferrer">{t('copilotAdoption.page.copilotUsageReportFaq')}</Link>,
                  windowDays: o.windowDays,
                })}
              </Text>
              <Text>
                {tNode('copilotAdoption.page.microsoftAlsoStatesUnlicensedCopilotChatUsageAvailableThrough', {
                  heading: <strong>{t('copilotAdoption.page.whyAuditLogStillRightSourceHere')}</strong>,
                  link: <Link href={MICROSOFT_COPILOT_USAGE_REPORT_API_URL} target="_blank" rel="noreferrer">{t('copilotAdoption.page.copilotUsageReportApi')}</Link>,
                })}
              </Text>
              <Text>
                <strong>{t('copilotAdoption.page.microsoftCopilotUsageReport')}</strong>{' '}
                {summary.dataSources.copilotUsageReportAvailable
                  ? t('copilotAdoption.page.snapshot', { v0: formatDate(summary.dataSources.copilotUsageReportDate) })
                  : t('copilotAdoption.page.notImported')}{' '}
                {t('copilotAdoption.page.licensedUsersOnlyUnavailableEntirelyWhenTenantConcealsUser')}
              </Text>
              <Text>
                <strong>{t('copilotAdoption.page.microsoftUsageReports')}</strong>{' '}
                {summary.dataSources.m365UsageReportsAvailable
                  ? t('copilotAdoption.page.snapshot2', { v0: formatDate(summary.dataSources.m365UsageReportDate) })
                  : t('copilotAdoption.page.notImported2')}{' '}
                {t('copilotAdoption.page.usedFindHeavyMicrosoftUsersHoldCopilotLicence')}
              </Text>
              <Text className={styles.muted}>
                {t('copilotAdoption.page.analysisGeneratedCovering', {
                  v0: formatDate(summary.generatedUtc),
                  v1: formatDate(summary.fromUtc),
                  v2: formatDate(summary.toUtc),
                })}
              </Text>
            </div>
          </AccordionPanel>
        </AccordionItem>

        <AccordionItem value="skus">
          <AccordionHeader>{t('copilotAdoption.page.whichSkusWereCountedCopilotLicences')}</AccordionHeader>
          <AccordionPanel>
            <div className={styles.method}>
              <Text>{t('copilotAdoption.page.microsoftShipsCopilotBrandedSkusMicrosoftCopilotLicenceCopilot')}</Text>
              <table className={styles.skuTable}>
                <thead>
                  <tr>
                    <th className={styles.skuCell}>{t('copilotAdoption.page.product')}</th>
                    <th className={styles.skuCell}>SKU</th>
                    <th className={styles.skuCell}>{t('copilotAdoption.page.assigned')}</th>
                    <th className={styles.skuCell}>{t('copilotAdoption.page.purchased')}</th>
                    <th className={styles.skuCell}>{t('copilotAdoption.page.unassigned')}</th>
                    <th className={styles.skuCell}>{t('copilotAdoption.page.assignedIdle')}</th>
                    <th className={styles.skuCell}>{t('copilotAdoption.page.countedCopilotLicence')}</th>
                  </tr>
                </thead>
                <tbody>
                  {(summary.seatLicenceTypes ?? []).map((licence) => (
                    <tr key={licence.id}>
                      <td className={styles.skuCell}>{licence.name}</td>
                      <td className={styles.skuCell}>
                        <Text size={200} className={styles.muted}>
                          {licence.skuPartNumber}
                        </Text>
                      </td>
                      <td className={styles.skuCell}>{formatCount(licence.assignedUsers)}</td>
                      <td className={styles.skuCell}>{licence.purchasedUnits == null ? t('copilotAdoption.page.unknown') : formatCount(licence.purchasedUnits)}</td>
                      <td className={styles.skuCell}>{licence.unassignedUnits == null ? t('copilotAdoption.page.unknown2') : formatCount(licence.unassignedUnits)}</td>
                      <td className={styles.skuCell}>{formatCount(licence.assignedIdleUsers)}</td>
                      <td className={styles.skuCell}>{licence.isCopilotSeat ? t('copilotAdoption.page.yes') : t('copilotAdoption.page.no')}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </AccordionPanel>
        </AccordionItem>
      </Accordion>
    </Card>
  );
}

/** The Executive view keeps only the board-pack headlines; the Analyst view keeps the full KPI set. */
function buildExecutiveKpis(
  summary: CopilotAdoptionSummary,
  t: TFunction,
  timeSaved: TimeSavedAssumptions,
  onOpenTab?: (tab: AdoptionTab) => void,
): KpiDefinition[] {
  const executiveKeys = new Set([
    'licensed',
    'adoption',
    'habit',
    'reclaim',
    'unlicensed',
    'candidates',
    'licenceTimeSaved',
    'coworkTimeSaved',
  ]);
  return buildKpis(summary, t, timeSaved, onOpenTab).filter((item) => executiveKeys.has(item.key));
}

/**
 * The two modelled time-back figures, promoted to the overview - one tile per decision.
 *
 * "Time back from licensing" sizes buying Copilot licences for the people recommended for one, with
 * minutes evidenced by published Copilot studies. "Time back from Cowork" sizes enabling Cowork, paid
 * for in Copilot Credits, for the people ready for it now - on top of what their licences already
 * save, and resting on an assumption no study has tested. They are never added together: they justify
 * different decisions on different evidence, and a sum would recreate the blended figure that let
 * Copilot's evidence stand behind Cowork's.
 *
 * Built from the same projections and assumptions as the tabs that explain them - the reader's own
 * for this session, or the product defaults - so the overview can never quote a model differently
 * from its tab. Each is badged and drawn as modelled, links to its tab, and is absent rather than a
 * modelled zero when there is nobody to model. The values use compact numbers where they are shorter
 * in the reader's language, so a seven-digit range still fits the tile and the "h" never wraps onto a
 * line of its own.
 */
function buildTimeSavedKpis(
  summary: CopilotAdoptionSummary,
  t: TFunction,
  assumptions: TimeSavedAssumptions,
  onOpenTab?: (tab: AdoptionTab) => void,
): KpiDefinition[] {
  const o = summary.options;
  const items: KpiDefinition[] = [];
  const percent = formatNumber(assumptions.conservativeRatio * 100, { maximumFractionDigits: 1 });
  const minutes = (value: number) => formatNumber(value, { maximumFractionDigits: 2 });

  const licence = projectLicenceTimeSaved(summary.licenceOpportunityEstimate, assumptions, o);
  if (licence) {
    items.push({
      key: 'licenceTimeSaved',
      label: t('copilotAdoption.page.kpi.licenceTimeSaved.label'),
      value: t('copilotAdoption.page.kpi.hoursValue', { range: compactHoursRange(t, licence.hoursLow, licence.hoursHigh) }),
      hint: t(
        plural(licence.cohortUsers, 'copilotAdoption.page.kpi.licenceTimeSaved.hint.one', 'copilotAdoption.page.kpi.licenceTimeSaved.hint.other'),
        { users: formatCount(licence.cohortUsers) },
      ),
      tone: 'opportunity',
      modelledBadge: t('copilotAdoption.page.kpi.modelledBadge'),
      action: onOpenTab
        ? { label: t('copilotAdoption.page.kpi.licenceTimeSaved.open'), onClick: () => onOpenTab('opportunities') }
        : undefined,
      info: {
        what: t('copilotAdoption.page.kpi.licenceTimeSaved.what'),
        how: t('copilotAdoption.page.kpi.licenceTimeSaved.how'),
        formula: t('copilotAdoption.page.kpi.licenceTimeSaved.formula', {
          meeting: minutes(assumptions.meetingMinutes),
          email: minutes(assumptions.emailMinutes),
          document: minutes(assumptions.documentMinutes),
          percent,
        }),
        source: t('copilotAdoption.page.kpi.licenceTimeSaved.source'),
      },
    });
  }

  if (summary.coworkReadinessAvailable) {
    // The people ready now lead, as on the Cowork tab: that is the spending-policy decision. When
    // nobody is ready, the ceiling stands in - and says it is every seat holder, not the ready few.
    const ready = projectCoworkTimeSaved(summary.coworkValueEstimate, assumptions, o);
    const cowork = ready ?? projectCoworkTimeSaved(summary.coworkFullRolloutEstimate, assumptions, o);
    if (cowork) {
      items.push({
        key: 'coworkTimeSaved',
        label: t('copilotAdoption.page.kpi.coworkTimeSaved.label'),
        value: t('copilotAdoption.page.kpi.hoursValue', { range: compactHoursRange(t, cowork.hoursLow, cowork.hoursHigh) }),
        hint: ready
          ? t(
              plural(ready.cohortUsers, 'copilotAdoption.page.kpi.coworkTimeSaved.hintReady.one', 'copilotAdoption.page.kpi.coworkTimeSaved.hintReady.other'),
              { users: formatCount(ready.cohortUsers) },
            )
          : t(
              plural(cowork.cohortUsers, 'copilotAdoption.page.kpi.coworkTimeSaved.hintCeiling.one', 'copilotAdoption.page.kpi.coworkTimeSaved.hintCeiling.other'),
              { users: formatCount(cowork.cohortUsers) },
            ),
        tone: 'opportunity',
        modelledBadge: t('copilotAdoption.page.kpi.modelledBadge'),
        action: onOpenTab
          ? { label: t('copilotAdoption.page.kpi.coworkTimeSaved.open'), onClick: () => onOpenTab('cowork') }
          : undefined,
        info: {
          what: t('copilotAdoption.page.kpi.coworkTimeSaved.what'),
          how: t('copilotAdoption.page.kpi.coworkTimeSaved.how'),
          formula: t('copilotAdoption.page.kpi.coworkTimeSaved.formula', {
            taskMinutes: minutes(assumptions.taskMinutes),
            percent,
          }),
          source: t('copilotAdoption.page.kpi.coworkTimeSaved.source'),
        },
      });
    }
  }

  return items;
}

/**
 * The headline figures used by the Analyst view. The Executive view filters this list down to the
 * board-pack subset so the two views cannot drift apart.
 */
function buildKpis(
  summary: CopilotAdoptionSummary,
  t: TFunction,
  timeSaved: TimeSavedAssumptions,
  onOpenTab?: (tab: AdoptionTab) => void,
): KpiDefinition[] {
  const o = summary.options;
  const seatSkus = (summary.seatLicenceTypes ?? []).filter((l) => l.isCopilotSeat);
  const scoreWeights = [o.frequencyWeight, o.depthWeight, o.breadthWeight];

  // Identical unless the detail query hit its row cap. When they differ, every rate below describes
  // the scored subset, and saying so is the difference between a caveat and a wrong number.
  const capped = summary.scoredUsers > 0 && summary.scoredUsers < summary.licensedUsers;
  const denominatorNote = capped
    ? ' ' + t('copilotAdoption.page.kpi.denominatorCappedNote', {
        scored: formatCount(summary.scoredUsers),
        total: formatCount(summary.licensedUsers),
      })
    : '';

  const items: KpiDefinition[] = [
    {
      key: 'licensed',
      label: t('copilotAdoption.page.copilotLicences'),
      value: formatCount(summary.licensedUsers),
      hint: capped
        ? t('copilotAdoption.page.kpi.licensedHintCapped', { scored: formatCount(summary.scoredUsers), skus: seatSkus.length })
        : t('copilotAdoption.page.kpi.licensedHint', { skus: seatSkus.length }),
      info: {
        what: t('copilotAdoption.page.peopleHoldingLeastOneLicenceToolClassifiedMicrosoftCopilot'),
        how: t('copilotAdoption.page.countedImportedLicenceAssignmentsDeDuplicatedPerUserSomeone'),
        source:
          t('copilotAdoption.page.needsUserMetadataImportLicenceAssignmentCountPurchaseCount'),
      },
    },
    {
      key: 'purchased',
      label: t('copilotAdoption.page.purchasedSeats'),
      value: summary.purchasedCopilotSeats == null ? t('copilotAdoption.page.unknown') : formatCount(summary.purchasedCopilotSeats),
      hint: summary.unassignedCopilotSeats == null
        ? t('copilotAdoption.page.kpi.grantOrganizationReadAll')
        : t('copilotAdoption.page.kpi.unassignedSeats', { count: formatCount(summary.unassignedCopilotSeats) }),
      tone: summary.unassignedCopilotSeats != null && summary.unassignedCopilotSeats > 0 ? 'critical' : undefined,
      info: {
        what: t('copilotAdoption.page.microsoftCopilotSeatsPurchasedTenantGraphSubscribedskusPrepaidunitsSeparate'),
        how: t('copilotAdoption.page.purchasedEnabledWarningSuspendedPrepaidUnitsSkusClassifiedMicrosoft'),
        source: summary.subscribedSkusAvailable
          ? t('copilotAdoption.page.kpi.purchasedSeatsSourceImported')
          : t('copilotAdoption.page.kpi.purchasedSeatsSourceUnknown'),
      },
    },
    {
      key: 'adoption',
      label: t('copilotAdoption.page.adoptionRate3'),
      value: formatPct(summary.adoptionRatePct),
      hint: t('copilotAdoption.page.ofUsedCopilotPeriod', { v0: formatCount(summary.activeUsers), v1: formatCount(summary.scoredUsers) }),
      tone: bandTone(summary.adoptionRatePct),
      info: {
        what: t('copilotAdoption.page.theShareLicensedUsersUsedCopilotLeastOnceSelected'),
        how: t('copilotAdoption.page.deliberatelyLowBarWeakestNumberPageOneInteractionDays', { v0: o.windowDays }),
        formula: t('copilotAdoption.page.active2', { v0: formatCount(summary.activeUsers), v1: formatCount(
          summary.scoredUsers,
        ), v2: t(capped ? 'copilotAdoption.page.population.analysed' : 'copilotAdoption.page.population.licensed'), v3: formatPct(summary.adoptionRatePct) }),
        source: t('copilotAdoption.page.activityComesCopilotAuditLogDayPeriodFallingBack', { v0: o.windowDays, v1: denominatorNote }),
      },
    },
    {
      key: 'habit',
      label: t('copilotAdoption.page.habitualUsers'),
      value: formatPct(summary.habitRatePct),
      hint: t('copilotAdoption.page.haveMadeCopilotPartWorkingWeek', { v0: formatCount(summary.habitualUsers) }),
      tone: summary.habitRatePct >= 50 ? 'good' : summary.habitRatePct >= 25 ? 'warning' : 'critical',
      info: {
        what: t('copilotAdoption.page.licensedUsersWhomCopilotRoutinePartWorkingWeekSomething'),
        how: t('copilotAdoption.page.userHabitualWhenEngagementScoreReachesOutEstablishedChampion', { v0: o.establishedScore }),
        formula: t('copilotAdoption.page.usersScoring', { v0: formatCount(summary.habitualUsers), v1: o.establishedScore, v2: formatCount(
          summary.scoredUsers,
        ), v3: t(capped ? 'copilotAdoption.page.population.analysed' : 'copilotAdoption.page.population.licensed'), v4: formatPct(summary.habitRatePct) }),
        source: t('copilotAdoption.page.thisFigureTracksRealisedValueAdoptionRateSitWhile', { v0: denominatorNote }),
      },
    },
    {
      key: 'reclaim',
      label: t('copilotAdoption.page.reclaimableLicences2'),
      value: formatCount(summary.reclaimableSeats),
      hint: t('copilotAdoption.page.certainProbableReviewExcluded', { v0: formatCount(summary.reclaimCertainSeats), v1: formatCount(
        summary.reclaimProbableSeats,
      ), v2: formatCount(summary.reclaimReviewSeats), v3: formatCount(summary.reclaimExcludedUsers) }),
      tone: summary.reclaimableSeats > 0 ? 'critical' : 'good',
      info: {
        what: t('copilotAdoption.page.licencesSafeEnoughIncludeActionableReclaimTotalDisabledAccounts'),
        how: t('copilotAdoption.page.newUserProtectedDaysUsingGraphUserCreateddatetimeAccount', { v0: o.reclaimGraceDays, v1: summary.reclaimCaveat ?? '' }),
        // Two independent mechanisms hold seats back - confidence tiering and a Microsoft
        // report-period mismatch - so the formula has to state both, or a reader adding up the band
        // breakdown finds a gap nothing on the page accounts for.
        formula: t('copilotAdoption.page.certainProbableReclaimableReviewOnlyExcludedUsersStillRemain', { v0: formatCount(summary.reclaimCertainSeats), v1: formatCount(
          summary.reclaimProbableSeats,
        ), v2: summary.reclaimSeatsHeldBackForWindowMismatch > 0
            ? ` - ${t('copilotAdoption.page.reclaimWindowMismatchDeduction', {
                count: formatCount(summary.reclaimSeatsHeldBackForWindowMismatch),
              })}`
            : '', v3: formatCount(summary.reclaimableSeats), v4: formatCount(
          summary.reclaimReviewSeats,
        ), v5: formatCount(
          summary.reclaimExcludedUsers,
        ), v6: formatCount(
          summary.neverUsedUsers,
        ), v7: formatCount(summary.dormantUsers), v8: formatCount(
          summary.reclaimSeatsFromActiveBands,
        ), v9: formatCount(summary.reclaimableSeats), v10: summary.reclaimSeatsHeldBackForWindowMismatch > 0
            ? ` + ${t('copilotAdoption.page.reclaimWindowMismatchAddBack', {
                count: formatCount(summary.reclaimSeatsHeldBackForWindowMismatch),
              })}`
            : '', v11: formatCount(
          summary.reclaimSeatsHeldBackForReview,
        ) }),
        source:
          t('copilotAdoption.page.drillThroughLicensedUsersTabReclaimTierFilterEach'),
      },
    },
    {
      key: 'disabled-reclaim',
      label: t('copilotAdoption.page.disabledSeats'),
      value: formatCount(summary.disabledLicensedUsers),
      hint: t('copilotAdoption.page.disabledAccountsStillHoldingCopilotLicence'),
      tone: summary.disabledLicensedUsers > 0 ? 'critical' : 'good',
      info: {
        what: t('copilotAdoption.page.copilotSeatsAssignedDisabledEntraAccountsRawInventoryActionable'),
        how: t('copilotAdoption.page.countedLicensedUserRowsAccountenabledFalseIncludingAdminExcluded'),
        formula: t('copilotAdoption.page.disabledLicensedAccountCertainReclaimsDifferenceAdminExcluded', { v0: formatCount(summary.disabledLicensedUsers), v1: formatCount(
          summary.reclaimCertainSeats,
        ) }),
        source: t('copilotAdoption.page.requiresGraphUserMetadataImportPopulatedAccountenabled'),
      },
    },
    {
      key: 'score',
      label: t('copilotAdoption.page.averageEngagement'),
      value: Math.round(summary.averageAdoptionScore),
      hint: t('copilotAdoption.page.median', { v0: Math.round(summary.medianAdoptionScore) }),
      info: {
        what: t('copilotAdoption.page.theMeanEngagementScoreAcrossAllLicensedUsersIncluding'),
        how: t('copilotAdoption.page.eachUserScoreOutCombinesFrequencyDepthBreadthUnused', { v0: formatPct(
          weightSharePct(o.frequencyWeight, scoreWeights),
        ), v1: formatPct(weightSharePct(o.depthWeight, scoreWeights)), v2: formatPct(
          weightSharePct(o.breadthWeight, scoreWeights),
        ) }),
        formula: t('copilotAdoption.page.meanMedian', { v0: Math.round(summary.averageAdoptionScore), v1: Math.round(
          summary.medianAdoptionScore,
        ) }),
        source:
          t('copilotAdoption.page.theMedianShownNextMeanHandfulChampionsPullMean'),
      },
    },
  ];

  // Cowork is only claimed as a metric when Cowork was actually seen: on a tenant that has not been
  // enabled for it, "0% Cowork adoption" reads as a failure rather than as "not available here".
  if (summary.coworkDetected) {
    items.push({
      key: 'cowork',
      label: summary.coworkAdoptionPct === null
        ? t('copilotAdoption.page.kpi.coworkUsageObserved')
        : t('copilotAdoption.page.coworkAdoption'),
      value: summary.coworkAdoptionPct === null ? formatCount(summary.coworkUsers) : formatPct(summary.coworkAdoptionPct),
      hint: summary.coworkReportTotalTasks > 0
        ? t('copilotAdoption.page.kpi.coworkTasksHint', {
            tasks: formatCount(summary.coworkReportTotalTasks),
            interactions: formatCount(summary.coworkInteractions),
          })
        : t('copilotAdoption.page.kpi.coworkAuditHint', { interactions: formatCount(summary.coworkInteractions) }),
      tone: 'opportunity',
      info: {
        what: summary.coworkAdoptionPct === null
          ? t('copilotAdoption.page.kpi.coworkWhatUnknownEligibility')
          : t('copilotAdoption.page.kpi.coworkWhatKnownEligibility'),
        how: t('copilotAdoption.page.microsoftCoworkUsageReportSuppliesTaskCountsAvailableAudit'),
        source:
          t('copilotAdoption.page.coworkEligibilityControlledSpendingPolicyScopeDeprecatedCoworkAgent'),
      },
    });
  }

  if (summary.unlicensedActiveUsers > 0) {
    items.push({
      key: 'unlicensed',
      label: t('copilotAdoption.page.usingCopilotUnlicensed'),
      value: formatCount(summary.unlicensedActiveUsers),
      hint: t('copilotAdoption.page.provenDemandAlreadyUsingCopilotChatNoLicence'),
      tone: 'opportunity',
      info: {
        what: t('copilotAdoption.page.peopleNoMicrosoftCopilotLicenceNeverthelessUsedCopilotPeriod'),
        how: t('copilotAdoption.page.countedCopilotAuditLogEveryUserHoldsNoneSkus'),
        source:
          t('copilotAdoption.page.thisInvisibleMicrosoftOwnCopilotUsageReportsCoverLicensed'),
      },
    });
  }

  items.push({
    key: 'candidates',
    label: t('copilotAdoption.page.recommendedLicence'),
    value: formatCount(summary.recommendedForLicence),
    hint: t('copilotAdoption.page.heavyMicrosoftUsersStrongBusinessCase'),
    tone: 'opportunity',
    info: {
      what: t('copilotAdoption.page.unlicensedUsersRecommendedLicenceEitherAlreadyUseCopilotLeast', { v0: o.opportunityProvenDemandMinActiveDays, v1: o.opportunityRecommendScore }),
      how: t('copilotAdoption.page.fourWeightedSignalsAlreadyUsingCopilotChatLicencePoints', { v0: o.opportunityUnlicensedCopilotWeight, v1: o.opportunityCollaborationWeight, v2: o.opportunityEmailWeight, v3: o.opportunityDocumentWeight }),
      formula: t('copilotAdoption.page.recommendedWhenUnlicensedcopilotactivedaysScore', { v0: o.opportunityProvenDemandMinActiveDays, v1: o.opportunityRecommendScore }),
      source:
        t('copilotAdoption.page.disabledAccountsUsersNoRecordedActivityAllExcludedMicrosoft'),
    },
  });

  // Last: a model follows the measurements it is built on, never leads them.
  items.push(...buildTimeSavedKpis(summary, t, timeSaved, onOpenTab));

  return items;
}

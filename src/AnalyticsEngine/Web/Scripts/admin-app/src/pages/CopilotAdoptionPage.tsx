import { useEffect, useState } from 'react';
import {
  makeStyles,
  tokens,
  Title3,
  Body1,
  Text,
  Card,
  Select,
  Tab,
  TabList,
  MessageBar,
  MessageBarBody,
  Accordion,
  AccordionHeader,
  AccordionItem,
  AccordionPanel,
  type SelectTabEventHandler,
} from '@fluentui/react-components';
import {
  fetchAdoptionAvailability,
  fetchAdoptionFilters,
  fetchAdoptionSql,
  fetchAdoptionSummary,
} from '../api/copilotAdoptionApi';
import type {
  AdoptionFilterOptions,
  CopilotAdoptionAvailability,
  CopilotAdoptionSummary,
} from '../types/copilotAdoption';
import Spinner from '../components/Spinner';
import SqlPopover from '../components/SqlPopover';
import TimeSeriesChart from '../components/charts/TimeSeriesChart';
import CategoryBarChart from '../components/charts/CategoryBarChart';
import AdoptionFunnel from '../components/copilotAdoption/AdoptionFunnel';
import LicensedUsersPanel from '../components/copilotAdoption/LicensedUsersPanel';
import OpportunitiesPanel from '../components/copilotAdoption/OpportunitiesPanel';
import { SegmentTable } from '../components/copilotAdoption/adoptionShared';
import { KpiGrid, formatCount, formatDate, formatPct } from '../components/copilotAdoption/KpiGrid';
import type { KpiDefinition } from '../components/copilotAdoption/KpiGrid';

const WINDOW_OPTIONS = [
  { value: 7, label: 'Last 7 days' },
  { value: 28, label: 'Last 28 days' },
  { value: 90, label: 'Last 90 days' },
  { value: 180, label: 'Last 180 days' },
];

type AdoptionTab = 'overview' | 'licensed' | 'opportunities' | 'method';

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
  cardHead: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '12px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  cardBody: {
    marginTop: '10px',
  },
  method: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    color: tokens.colorNeutralForeground2,
    maxWidth: '860px',
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

  const [availability, setAvailability] = useState<CopilotAdoptionAvailability | null>(null);
  const [availabilityError, setAvailabilityError] = useState<string | null>(null);
  const [windowDays, setWindowDays] = useState(28);
  const [tab, setTab] = useState<AdoptionTab>('overview');

  const [summary, setSummary] = useState<CopilotAdoptionSummary | null>(null);
  const [summaryLoading, setSummaryLoading] = useState(true);
  const [summaryError, setSummaryError] = useState<string | null>(null);
  const [filterOptions, setFilterOptions] = useState<AdoptionFilterOptions | null>(null);
  const [sql, setSql] = useState<Record<string, string> | null>(null);

  useEffect(() => {
    let cancelled = false;
    fetchAdoptionAvailability()
      .then((a) => {
        if (!cancelled) setAvailability(a);
      })
      .catch((e) => {
        if (!cancelled) {
          setAvailabilityError(e instanceof Error ? e.message : 'Failed to check Copilot adoption availability.');
        }
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (!availability?.available) {
      setSummaryLoading(false);
      return;
    }

    let cancelled = false;
    setSummaryLoading(true);
    setSummaryError(null);

    fetchAdoptionSummary(windowDays)
      .then((s) => {
        if (!cancelled) setSummary(s);
      })
      .catch((e) => {
        if (!cancelled) setSummaryError(e instanceof Error ? e.message : 'Failed to load the adoption summary.');
      })
      .finally(() => {
        if (!cancelled) setSummaryLoading(false);
      });

    // The filter lists and the SQL come from the same cached analysis, so these are cheap follow-ups
    // rather than extra work. Their failure is not worth surfacing - the page works without them.
    fetchAdoptionFilters(windowDays)
      .then((f) => {
        if (!cancelled) setFilterOptions(f);
      })
      .catch(() => undefined);

    fetchAdoptionSql(windowDays)
      .then((s) => {
        if (!cancelled) setSql(s);
      })
      .catch(() => undefined);

    return () => {
      cancelled = true;
    };
  }, [availability, windowDays]);

  const onTabSelect: SelectTabEventHandler = (_e, data) => setTab(data.value as AdoptionTab);

  return (
    <div>
      <div className={styles.header}>
        <div>
          <Title3>Copilot Adoption</Title3>
          <Body1 block className={styles.intro}>
            Who is paying for a Microsoft 365 Copilot licence they are not using, and who would benefit from one
            they do not have. Both lists export to CSV with full user metadata, so they can be handed to a
            department lead or attached to a licence request.
          </Body1>
        </div>
        <div className={styles.controls}>
          <Text size={200} className={styles.muted}>
            Period
          </Text>
          <Select
            value={String(windowDays)}
            onChange={(_e, d) => setWindowDays(Number(d.value))}
            aria-label="Reporting period"
          >
            {WINDOW_OPTIONS.map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </Select>
        </div>
      </div>

      {availabilityError && (
        <MessageBar intent="error" style={{ marginTop: '16px' }}>
          <MessageBarBody>{availabilityError}</MessageBarBody>
        </MessageBar>
      )}

      {availability && !availability.available && (
        <MessageBar intent="info" style={{ marginTop: '16px' }}>
          <MessageBarBody>
            Copilot adoption reporting is not available on this deployment.
            <ul style={{ margin: '6px 0 0 0', paddingInlineStart: '20px' }}>
              {availability.messages.map((m) => (
                <li key={m}>{m}</li>
              ))}
            </ul>
          </MessageBarBody>
        </MessageBar>
      )}

      {availability?.available && (
        <>
          {availability.messages.length > 0 && (
            <MessageBar intent="warning" style={{ marginTop: '16px' }}>
              <MessageBarBody>
                <ul style={{ margin: 0, paddingInlineStart: '20px' }}>
                  {availability.messages.map((m) => (
                    <li key={m}>{m}</li>
                  ))}
                </ul>
              </MessageBarBody>
            </MessageBar>
          )}

          <div className={styles.subTabs}>
            <TabList selectedValue={tab} onTabSelect={onTabSelect}>
              <Tab value="overview">Overview</Tab>
              <Tab value="licensed">Licensed users</Tab>
              <Tab value="opportunities">Licence opportunities</Tab>
              <Tab value="method">How this is calculated</Tab>
            </TabList>
          </div>

          {summaryLoading && (
            <div style={{ textAlign: 'center', padding: '32px' }}>
              <Spinner size={80} label="Analysing Copilot adoption..." />
            </div>
          )}

          {summaryError && (
            <MessageBar intent="error" style={{ marginTop: '16px' }}>
              <MessageBarBody>{summaryError}</MessageBarBody>
            </MessageBar>
          )}

          {!summaryLoading && summary && (
            <div className={styles.stack}>
              {summary.warnings.length > 0 && (
                <MessageBar intent="warning">
                  <MessageBarBody>
                    <ul style={{ margin: 0, paddingInlineStart: '20px' }}>
                      {summary.warnings.map((w) => (
                        <li key={w}>{w}</li>
                      ))}
                    </ul>
                  </MessageBarBody>
                </MessageBar>
              )}

              {tab === 'overview' && (
                <OverviewTab summary={summary} sql={sql} />
              )}

              {tab === 'licensed' && (
                <LicensedUsersPanel windowDays={windowDays} filterOptions={filterOptions} />
              )}

              {tab === 'opportunities' && (
                <OpportunitiesPanel windowDays={windowDays} filterOptions={filterOptions} />
              )}

              {tab === 'method' && <MethodTab summary={summary} />}
            </div>
          )}
        </>
      )}
    </div>
  );
}

/** The executive view: headline figures, the funnel, and where the gaps are. */
function OverviewTab({ summary, sql }: { summary: CopilotAdoptionSummary; sql: Record<string, string> | null }) {
  const styles = useStyles();
  const kpis = buildKpis(summary);

  return (
    <>
      <KpiGrid items={kpis} />

      <Card>
        <div className={styles.cardHead}>
          <div>
            <Text weight="semibold" size={400}>
              Adoption funnel
            </Text>
            <Text size={200} block className={styles.muted}>
              Every stage is a subset of the one above it. The biggest drop is where the effort should go.
            </Text>
          </div>
          {sql?.licensedUsers && <SqlPopover sql={sql.licensedUsers} title="SQL behind these figures" />}
        </div>
        <div className={styles.cardBody}>
          <AdoptionFunnel stages={summary.funnel} />
        </div>
      </Card>

      <div className={styles.twoUp}>
        <Card>
          <Text weight="semibold" size={400}>
            Engagement distribution
          </Text>
          <Text size={200} block className={styles.muted}>
            Licensed users by engagement band. "Never used" and "Dormant" together are the reclaimable seats.
          </Text>
          <div className={styles.cardBody}>
            <CategoryBarChart categories={summary.bandBreakdown} valueLabel="Users" />
          </div>
        </Card>

        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                Where Copilot is used
              </Text>
              <Text size={200} block className={styles.muted}>
                Interactions by app across licensed users. Often the fastest way to spot an unused surface.
              </Text>
            </div>
            {sql?.usageByApp && <SqlPopover sql={sql.usageByApp} title="SQL behind this chart" />}
          </div>
          <div className={styles.cardBody}>
            {summary.usageByApp.length > 0 ? (
              <CategoryBarChart categories={summary.usageByApp} valueLabel="Interactions" />
            ) : (
              <Text className={styles.muted}>
                No per-app breakdown is available. This needs the Copilot audit import.
              </Text>
            )}
          </div>
        </Card>
      </div>

      {summary.weeklyTrend.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                Weekly active licensed users
              </Text>
              <Text size={200} block className={styles.muted}>
                A single adoption rate cannot show whether an enablement programme is working. This can.
                {summary.coworkDetected && ' The second line tracks Microsoft 365 Copilot Cowork adoption.'}
              </Text>
            </div>
            {sql?.weeklyTrend && <SqlPopover sql={sql.weeklyTrend} title="SQL behind this chart" />}
          </div>
          <div className={styles.cardBody}>
            <TimeSeriesChart series={summary.weeklyTrend} valueLabel="Users" />
          </div>
        </Card>
      )}

      <Card>
        <Text weight="semibold" size={400}>
          Adoption by department
        </Text>
        <Text size={200} block className={styles.muted}>
          Lowest adoption first - the running order for an enablement plan. Departments with fewer than{' '}
          {summary.options.minSeatsPerSegment} seats are omitted because the percentage would not be meaningful.
        </Text>
        <div className={styles.cardBody}>
          <SegmentTable rows={summary.adoptionByDepartment} segmentLabel="Department" />
        </div>
      </Card>

      {summary.adoptionByCountry.length > 0 && (
        <Card>
          <Text weight="semibold" size={400}>
            Adoption by country
          </Text>
          <Text size={200} block className={styles.muted}>
            For organisations that run enablement regionally.
          </Text>
          <div className={styles.cardBody}>
            <SegmentTable rows={summary.adoptionByCountry} segmentLabel="Country" />
          </div>
        </Card>
      )}

      {summary.opportunityByDepartment.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                Where the unmet demand is
              </Text>
              <Text size={200} block className={styles.muted}>
                Departments with the most recommended licence candidates. Pair this with the department adoption
                table above: a department with unused seats and strong candidates can often be rebalanced at no cost.
              </Text>
            </div>
            {sql?.licenceOpportunities && <SqlPopover sql={sql.licenceOpportunities} title="SQL behind this chart" />}
          </div>
          <div className={styles.cardBody}>
            <CategoryBarChart categories={summary.opportunityByDepartment} valueLabel="Candidates" />
          </div>
        </Card>
      )}
    </>
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
  const o = summary.options;

  return (
    <Card>
      <Accordion multiple collapsible defaultOpenItems={['score']}>
        <AccordionItem value="score">
          <AccordionHeader>How the engagement score is calculated</AccordionHeader>
          <AccordionPanel>
            <div className={styles.method}>
              <Text>
                Each licensed user gets a score out of 100 built from three components, because "did they use
                Copilot?" is almost never a yes/no question. Someone who opened it twice and someone who lives in
                it produce the same "active user" count and need opposite responses.
              </Text>
              <Text>
                <strong>Frequency ({formatPct(o.frequencyWeight * 100)} of the score).</strong> Days used in the
                period, against a target of {formatPct(o.frequencyTargetRatio * 100)} of the available working
                days (assuming {o.workingDaysPerWeek} working days a week). Working days rather than calendar days
                - otherwise a genuinely daily user would top out at about 71%.
              </Text>
              <Text>
                <strong>Depth ({formatPct(o.depthWeight * 100)}).</strong> Interactions per active day, against a
                target of {o.depthTargetInteractionsPerActiveDay} per day. Separates "opened it once that day" from
                "worked with it that day".
              </Text>
              <Text>
                <strong>Breadth ({formatPct(o.breadthWeight * 100)}).</strong> How many Copilot surfaces (Teams,
                Word, Outlook, Copilot Chat and so on) they use, against a target of {o.breadthTargetApps}. Users
                who only ever use one surface are the cheapest group to move.
              </Text>
              <Text>
                <strong>Bands.</strong> Champion at {o.championScore}+, Established at {o.establishedScore}+,
                Developing at {o.developingScore}+, Trialling below that. Users with no activity in the period are
                split into <em>Dormant</em> (used Copilot within the last {o.historyDays} days but not in this
                period) and <em>Never used</em> - the distinction matters, because one needs a conversation and the
                other needs onboarding or the seat back.
              </Text>
            </div>
          </AccordionPanel>
        </AccordionItem>

        <AccordionItem value="opportunity">
          <AccordionHeader>How licence candidates are ranked</AccordionHeader>
          <AccordionPanel>
            <div className={styles.method}>
              <Text>
                Unlicensed users are scored out of 100 on four weighted signals, with the weighting set so that
                evidence beats inference.
              </Text>
              <Text>
                <strong>Already using Copilot Chat without a licence</strong> carries the most weight. It is the
                only signal that proves demand for Copilot itself rather than inferring it from general activity,
                and it is invisible in Microsoft's own reports, which cover licensed users only.
              </Text>
              <Text>
                <strong>Teams collaboration</strong>, <strong>email volume</strong> and{' '}
                <strong>document work</strong> make up the rest. They identify heavy knowledge workers who would
                benefit but have never had the chance to try it. Anyone scoring{' '}
                {o.opportunityRecommendScore} or above is marked as recommended.
              </Text>
              <Text>
                Disabled accounts are excluded from the candidate list. They are, however, kept in the licensed
                user list - a disabled account still holding a Copilot seat is the clearest reclaim there is.
              </Text>
            </div>
          </AccordionPanel>
        </AccordionItem>

        <AccordionItem value="sources">
          <AccordionHeader>Where the data comes from</AccordionHeader>
          <AccordionPanel>
            <div className={styles.method}>
              <Text>
                <strong>Copilot audit log:</strong>{' '}
                {summary.dataSources.auditAvailable ? 'available' : 'no data for this period'}. Covers every user,
                including unlicensed Copilot Chat use, and matches the selected period exactly.
              </Text>
              <Text>
                <strong>Microsoft Copilot usage report:</strong>{' '}
                {summary.dataSources.copilotUsageReportAvailable
                  ? `snapshot of ${formatDate(summary.dataSources.copilotUsageReportDate)}`
                  : 'not imported'}
                . Licensed users only, and unavailable entirely when the tenant conceals user information.
              </Text>
              <Text>
                <strong>Microsoft 365 usage reports:</strong>{' '}
                {summary.dataSources.m365UsageReportsAvailable
                  ? `snapshot of ${formatDate(summary.dataSources.m365UsageReportDate)}`
                  : 'not imported'}
                . Used to find heavy Microsoft 365 users who do not hold a Copilot licence.
              </Text>
              <Text className={styles.muted}>
                Analysis generated {formatDate(summary.generatedUtc)} covering{' '}
                {formatDate(summary.fromUtc)} to {formatDate(summary.toUtc)}.
              </Text>
            </div>
          </AccordionPanel>
        </AccordionItem>

        <AccordionItem value="skus">
          <AccordionHeader>Which licences were counted as Copilot seats</AccordionHeader>
          <AccordionPanel>
            <div className={styles.method}>
              <Text>
                Microsoft ships Copilot-branded SKUs that are not a Microsoft 365 Copilot seat (Copilot Studio,
                Copilot for Sales), and ships new seat SKUs regularly. Everything the tool found is listed below so
                the licensed population can be checked rather than taken on trust.
              </Text>
              <table className={styles.skuTable}>
                <thead>
                  <tr>
                    <th className={styles.skuCell}>Product</th>
                    <th className={styles.skuCell}>SKU</th>
                    <th className={styles.skuCell}>Users</th>
                    <th className={styles.skuCell}>Counted as a Copilot seat</th>
                  </tr>
                </thead>
                <tbody>
                  {summary.seatLicenceTypes.map((licence) => (
                    <tr key={licence.id}>
                      <td className={styles.skuCell}>{licence.name}</td>
                      <td className={styles.skuCell}>
                        <Text size={200} className={styles.muted}>
                          {licence.skuPartNumber}
                        </Text>
                      </td>
                      <td className={styles.skuCell}>{formatCount(licence.assignedUsers)}</td>
                      <td className={styles.skuCell}>{licence.isCopilotSeat ? 'Yes' : 'No'}</td>
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

/**
 * The headline figures, in the order an executive reads them: how many seats, how many are working,
 * how many are wasted, and what the unmet demand is.
 */
function buildKpis(summary: CopilotAdoptionSummary): KpiDefinition[] {
  const items: KpiDefinition[] = [
    {
      key: 'licensed',
      label: 'Copilot licences',
      value: formatCount(summary.licensedUsers),
      hint: `${summary.seatLicenceTypes.filter((l) => l.isCopilotSeat).length} seat SKU(s) counted`,
    },
    {
      key: 'adoption',
      label: 'Adoption rate',
      value: formatPct(summary.adoptionRatePct),
      hint: `${formatCount(summary.activeUsers)} used Copilot in this period`,
      tone: summary.adoptionRatePct >= 70 ? 'good' : summary.adoptionRatePct >= 40 ? 'warning' : 'critical',
    },
    {
      key: 'habit',
      label: 'Habitual users',
      value: formatPct(summary.habitRatePct),
      hint: `${formatCount(summary.habitualUsers)} have made Copilot part of the working week`,
      tone: summary.habitRatePct >= 50 ? 'good' : summary.habitRatePct >= 25 ? 'warning' : 'critical',
    },
    {
      key: 'reclaim',
      label: 'Reclaimable seats',
      value: formatCount(summary.reclaimableSeats),
      hint: `${formatCount(summary.neverUsedUsers)} never used, ${formatCount(summary.dormantUsers)} dormant`,
      tone: summary.reclaimableSeats > 0 ? 'critical' : 'good',
    },
    {
      key: 'score',
      label: 'Average engagement',
      value: Math.round(summary.averageAdoptionScore),
      hint: `Median ${Math.round(summary.medianAdoptionScore)} of 100`,
    },
  ];

  // Cowork is only claimed as a metric when Cowork was actually seen: on a tenant that has not been
  // enabled for it, "0% Cowork adoption" reads as a failure rather than as "not available here".
  if (summary.coworkDetected) {
    items.push({
      key: 'cowork',
      label: 'Cowork adoption',
      value: formatPct(summary.coworkAdoptionPct),
      hint: `${formatCount(summary.coworkUsers)} licensed users, ${formatCount(
        summary.coworkInteractions,
      )} interactions`,
      tone: 'opportunity',
    });
  }

  if (summary.unlicensedActiveUsers > 0) {
    items.push({
      key: 'unlicensed',
      label: 'Using Copilot unlicensed',
      value: formatCount(summary.unlicensedActiveUsers),
      hint: 'Proven demand - already using Copilot Chat with no seat',
      tone: 'opportunity',
    });
  }

  items.push({
    key: 'candidates',
    label: 'Recommended for a licence',
    value: formatCount(summary.recommendedForLicence),
    hint: 'Heavy Microsoft 365 users with a strong business case',
    tone: 'opportunity',
  });

  return items;
}

import { useEffect, useState } from 'react';
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
import GaugeRing from '../components/charts/GaugeRing';
import RadarChart from '../components/charts/RadarChart';
import AdoptionFunnel from '../components/copilotAdoption/AdoptionFunnel';
import LicensedUsersPanel from '../components/copilotAdoption/LicensedUsersPanel';
import OpportunitiesPanel from '../components/copilotAdoption/OpportunitiesPanel';
import HabitStrip from '../components/copilotAdoption/HabitStrip';
import IntensityScatter from '../components/copilotAdoption/IntensityScatter';
import ActionPlan from '../components/copilotAdoption/ActionPlan';
import AgentsPanel from '../components/copilotAdoption/AgentsPanel';
import UnlicensedPanel from '../components/copilotAdoption/UnlicensedPanel';
import { ConcentrationBar, CombinedSegmentTable } from '../components/copilotAdoption/CombinedViews';
import InfoTip from '../components/copilotAdoption/InfoTip';
import { SegmentTable, BAND_COLOUR_LIST } from '../components/copilotAdoption/adoptionShared';
import { KpiGrid, formatCount, formatDate, formatPct, weightSharePct } from '../components/copilotAdoption/KpiGrid';
import type { KpiDefinition } from '../components/copilotAdoption/KpiGrid';

const WINDOW_OPTIONS = [
  { value: 7, label: 'Last 7 days' },
  { value: 28, label: 'Last 28 days' },
  { value: 90, label: 'Last 90 days' },
  { value: 180, label: 'Last 180 days' },
];

type AdoptionTab = 'overview' | 'licensed' | 'unlicensed' | 'agents' | 'opportunities' | 'method';

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
          {availability?.available && (
            <Tooltip
              relationship="description"
              content="The whole report - every figure, table and chart - as an Excel workbook with live, editable charts. Run it before and after an enablement programme to compare like for like."
            >
              <Button
                appearance="primary"
                icon={<ArrowDownload16Regular />}
                as="a"
                href={workbookExportUrl(windowDays)}
              >
                Excel report
              </Button>
            </Tooltip>
          )}
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
              <Tab value="unlicensed">Unlicensed usage</Tab>
              <Tab value="agents">Agents</Tab>
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
                <LicensedUsersPanel
                  windowDays={windowDays}
                  filterOptions={filterOptions}
                  actionPlan={summary.actionPlan}
                  options={summary.options}
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

              {tab === 'opportunities' && (
                <OpportunitiesPanel
                  windowDays={windowDays}
                  filterOptions={filterOptions}
                  options={summary.options}
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

/** The executive view: headline figures, the funnel, and where the gaps are. */
function OverviewTab({ summary, sql }: { summary: CopilotAdoptionSummary; sql: Record<string, string> | null }) {
  const styles = useStyles();
  const kpis = buildKpis(summary);
  const o = summary.options;

  // The band slices and the action plan are built from the users actually scored, which is capped by
  // MaxLicensedUsersScored. That cap is far above any real Copilot deployment and raises an explicit
  // warning when it bites - but the donut must still add up to what it is drawing, so the centre is
  // the sum of the slices rather than the licence count.
  const analysedUsers = summary.bandBreakdown.reduce((sum, b) => sum + b.value, 0);

  return (
    <>
      <KpiGrid items={kpis} />

      <Card>
        <div className={styles.cardHead}>
          <div>
            <Text weight="semibold" size={400}>
              Where you stand
            </Text>
            <Text size={200} block className={styles.muted}>
              The two rates that decide whether the seats are earning their keep, against the scale this tool
              judges them on.
            </Text>
          </div>
          <InfoTip
            title="Where you stand"
            content={{
              what: 'Adoption rate is the share of licensed users who touched Copilot at all. Habit rate is the share for whom it is a routine part of the working week.',
              how: `The coloured arc is the judgement scale, not a smooth gradient - a continuous ramp would imply the difference between 41% and 43% means something, and it does not. Below 40% needs attention, 40-70% is progressing, above 70% is healthy. Habit is measured at an engagement score of ${o.establishedScore} or more.`,
              source:
                'The gap between the two gauges is the finding. Adoption at 100% with a habit rate near zero means everyone opened it once - which is exactly the situation a renewal conversation needs to surface, and which a single adoption figure conceals.',
            }}
          />
        </div>
        <div className={`${styles.cardBody} ${styles.gauges}`}>
          <GaugeRing
            value={summary.adoptionRatePct}
            label="Adoption rate"
            sublabel={`${formatCount(summary.activeUsers)} of ${formatCount(
              summary.licensedUsers,
            )} licensed users touched Copilot`}
          />
          <GaugeRing
            value={summary.habitRatePct}
            label="Habit rate"
            sublabel={`${formatCount(summary.habitualUsers)} have made it part of the working week`}
          />
          {summary.coworkDetected && (
            <GaugeRing
              value={summary.coworkAdoptionPct}
              label="Cowork adoption"
              sublabel={`${formatCount(summary.coworkUsers)} licensed users have used Cowork`}
            />
          )}
        </div>
      </Card>

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
          <div className={styles.cardTools}>
            <InfoTip
              title="Adoption funnel"
              content={{
                what: 'The licensed population narrowed one stage at a time, so the single biggest loss of value is visible rather than averaged away.',
                how: `Licensed = holders of a Copilot seat SKU. Ever used = any Copilot activity in the last ${o.historyDays} days. Active this period = at least one interaction inside the selected period. Habitual = engagement of ${o.establishedScore} or more. Champions = ${o.championScore} or more. The percentage on the right is the conversion from the stage above, not from the top - a 90% that follows a 40% is still a healthy step.`,
                source:
                  'Licensed counts come from the imported licence assignments; every activity stage comes from the Copilot audit log, falling back to Microsoft\u2019s per-user usage report where the audit import is unavailable.',
              }}
            />
            {sql?.licensedUsers && <SqlPopover sql={sql.licensedUsers} title="SQL behind these figures" />}
          </div>
        </div>
        <div className={styles.cardBody}>
          <AdoptionFunnel stages={summary.funnel} />
        </div>
      </Card>

      <Card>
        <div className={styles.cardHead}>
          <div>
            <Text weight="semibold" size={400}>
              Habit formation
            </Text>
            <Text size={200} block className={styles.muted}>
              Of the licensed users who used Copilot at all, how many days a month do they actually open it?
            </Text>
          </div>
          <InfoTip
            title="Habit formation"
            content={{
              what: 'Active licensed users split by how often they use Copilot, with no weighting applied at all - just distinct active days.',
              how: `Active days in the selected period are restated as days per ${o.habitBucketNormalisationDays}-day month, then rounded to whole days, so the tiles mean the same thing whichever period is chosen and the captions describe the comparison exactly. Infrequent is 1-${
                o.habitModerateMinDays - 1
              }, Moderate ${o.habitModerateMinDays}-${o.habitFrequentMinDays - 1}, Frequent ${
                o.habitFrequentMinDays
              }-${o.habitDailyMinDays - 1}, Daily ${o.habitDailyMinDays}+.`,
              formula: `daysPerMonth = round(activeDays x ${o.habitBucketNormalisationDays} / ${o.windowDays})`,
              source:
                'Percentages are of active users, not of all seats. Seats with no activity at all are counted in "reclaimable seats" instead - calling someone who never opened Copilot "infrequent" would hide the more expensive problem.',
            }}
          />
        </div>
        <div className={styles.cardBody}>
          <HabitStrip buckets={summary.habitBuckets} />
        </div>
      </Card>

      <div className={styles.twoUp}>
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                Engagement mix
              </Text>
              <Text size={200} block className={styles.muted}>
                Every licensed user in exactly one band. "Never used" and "Dormant" together are the
                reclaimable seats.
              </Text>
            </div>
            <InfoTip
              title="Engagement mix"
              content={{
                what: 'The whole licensed population split into six mutually exclusive engagement bands.',
                how: `Champion ${o.championScore}+, Established ${o.establishedScore}+, Developing ${o.developingScore}+, Trialling below that. Anyone with no activity in the period is not scored: they are Dormant if they used Copilot at some point in the last ${o.historyDays} days, Never used otherwise.`,
                source:
                  'The two zero-activity bands are separated because they need opposite responses - one needs a conversation about what went wrong, the other needs onboarding or the seat taken back.',
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <DonutChart
              categories={summary.bandBreakdown}
              colours={BAND_COLOUR_LIST}
              centreValue={formatCount(analysedUsers)}
              centreLabel="licensed users"
            />
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
            <div className={styles.cardTools}>
              <InfoTip
                title="Where Copilot is used"
                content={{
                  what: 'Total Copilot interactions in the period by the app they happened in, sized by area.',
                  how: `Every interaction in the Copilot audit log for a licensed user is attributed to the app that produced it, and the top ${o.topSegments} are shown. This counts interactions, not people, so one very heavy user can dominate a surface - read it alongside the breadth component of the engagement score, which counts surfaces per person.`,
                  source:
                    'Needs the Copilot audit import. Microsoft\u2019s own usage report does not break usage down this way.',
                }}
              />
              {sql?.usageByApp && <SqlPopover sql={sql.usageByApp} title="SQL behind this chart" />}
            </div>
          </div>
          <div className={styles.cardBody}>
            {summary.usageByApp.length > 0 ? (
              <TreemapChart categories={summary.usageByApp} valueLabel="interactions" />
            ) : (
              <Text className={styles.muted}>
                No per-app breakdown is available. This needs the Copilot audit import.
              </Text>
            )}
          </div>
        </Card>
      </div>

      <Card>
        <div className={styles.cardHead}>
          <div>
            <Text weight="semibold" size={400}>
              Enablement plan
            </Text>
            <Text size={200} block className={styles.muted}>
              Every licensed user needs exactly one of these next steps. This is the size of each job.
            </Text>
          </div>
          <InfoTip
            title="Enablement plan"
            content={{
              what: 'The per-user recommended actions, aggregated. Each action is stated once with the number of people who need it.',
              how: `Derived from the engagement band, and for the middle bands from the breadth score as well - a user with a genuine habit confined to one Copilot surface needs broadening rather than more coaching. The full per-user list, filterable and exportable, is on the "Licensed users" tab.`,
              source: `Ordered by size. Every user gets exactly one action, so the counts sum to the ${formatCount(
                analysedUsers,
              )} licensed users this analysis scored. If that is fewer than the licence count, a warning at the top of the page says so.`,
            }}
          />
        </div>
        <div className={styles.cardBody}>
          <ActionPlan actions={summary.actionPlan} />
        </div>
      </Card>

      {summary.scoreProfiles.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                The shape of adoption
              </Text>
              <Text size={200} block className={styles.muted}>
                Where your typical user differs from your best ones - and therefore what an enablement
                programme should actually target.
              </Text>
            </div>
            <InfoTip
              title="The shape of adoption"
              content={{
                what: 'The three components of the engagement score, averaged for the typical active user and for your Champions, plotted on one 0-100 scale.',
                how: 'Averaged over active users only - an idle seat scores zero on all three, which drags the whole profile inwards and says nothing about shape. Both series use the identical scale, so the two outlines are directly comparable.',
                source:
                  'The gap between the two outlines is the finding, not their size. If your average user matches your Champions on frequency but not breadth, more training on how often to use Copilot is wasted effort - they already use it often enough, they just use it in one place. The overall score is identical whichever of the three is missing.',
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <RadarChart
              axes={['Frequency', 'Depth', 'Breadth']}
              series={summary.scoreProfiles.map((p, i) => ({
                name: `${p.label} (${formatCount(p.users)})`,
                colour: i === 0 ? '#0f6cbd' : '#107c10',
                values: [p.frequencyScore, p.depthScore, p.breadthScore],
              }))}
            />
          </div>
        </Card>
      )}

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
            <div className={styles.cardTools}>
              <InfoTip
                title="Weekly active licensed users"
                content={{
                  what: 'Distinct licensed users with at least one Copilot interaction in each calendar week.',
                  how: 'Weeks start on a Monday and are counted in UTC. A user active on three days of a week counts once for that week. Weeks with no data are drawn as zero rather than skipped, so a gap in the import is visible instead of being smoothed over by the line.',
                  source:
                    'Six months of history regardless of the period selected above, because a trend is the one thing the period drop-down cannot show. Needs the Copilot audit import.',
                }}
              />
              {sql?.weeklyTrend && <SqlPopover sql={sql.weeklyTrend} title="SQL behind this chart" />}
            </div>
          </div>
          <div className={styles.cardBody}>
            <TimeSeriesChart series={summary.weeklyTrend} valueLabel="Users" />
          </div>
        </Card>
      )}

      {summary.weeklyVolumeTrend.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                Weekly Copilot volume
              </Text>
              <Text size={200} block className={styles.muted}>
                Interactions rather than people, licensed against unlicensed. Headcount can flatten while
                volume keeps climbing, and that is a different story.
              </Text>
            </div>
            <InfoTip
              title="Weekly Copilot volume"
              content={{
                what: 'Total Copilot interactions each week, split by whether the person holds a Copilot seat.',
                how: 'Counts interactions, not people. Drawn separately from the active-user chart on purpose: a few hundred users and tens of thousands of interactions share no sensible axis, and plotting them together flattens the user line onto zero.',
                source:
                  'Both series come from one pass over the Copilot audit log. The unlicensed line is the volume Microsoft\u2019s own reporting cannot see.',
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <TimeSeriesChart series={summary.weeklyVolumeTrend} valueLabel="Interactions" />
          </div>
        </Card>
      )}

      {summary.weeklyVolumeTrend.length > 1 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                Who is doing the Copilot work
              </Text>
              <Text size={200} block className={styles.muted}>
                The same weekly volume as composition rather than comparison - total height is all the Copilot
                activity in the organisation, and the bands are who is producing it.
              </Text>
            </div>
            <InfoTip
              title="Who is doing the Copilot work"
              content={{
                what: 'Weekly Copilot interactions stacked, so the total and its make-up are readable at once.',
                how: 'Drawn from the same series as the volume chart above. Weeks with no data for a band are treated as zero rather than interpolated - inventing activity that did not happen is worse than a visible dip.',
                source:
                  'Worth stating the trade-off: only the bottom band sits on a flat baseline, so only it can be read precisely. That is acceptable when the message is the mix, which is why the plain line chart above is kept rather than replaced. A rising unlicensed band against a flat licensed one is the clearest possible case for reallocating seats.',
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <StackedAreaChart series={summary.weeklyVolumeTrend} valueLabel="Interactions" />
          </div>
        </Card>
      )}

      {summary.concentration.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                How concentrated usage is
              </Text>
              <Text size={200} block className={styles.muted}>
                Share of all Copilot activity by cohort of active licensed users, heaviest first.
              </Text>
            </div>
            <InfoTip
              title="How concentrated usage is"
              content={{
                what: 'Active licensed users ranked by interaction count and cut into cohorts, showing what share of all activity each accounts for.',
                how: 'Only users who were active at least once are ranked - including idle seats would put every one of them in the bottom cohort at zero and give every tenant the same chart. Percentile cohorts rather than fixed counts, so the shape is comparable between a 50-seat tenant and a 50,000-seat one.',
                source:
                  'This is the figure an adoption percentage hides. "40% adoption spread evenly" and "40% adoption where a tenth of them do most of it" are the same percentage and completely different situations - the second collapses when those people change team.',
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <ConcentrationBar bands={summary.concentration} />
          </div>
        </Card>
      )}

      {summary.combinedByDepartment.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                Licensed and unlicensed, side by side
              </Text>
              <Text size={200} block className={styles.muted}>
                A department with idle seats and heavy unlicensed use is a seat-allocation problem, not an
                adoption problem - and it can usually be fixed at no cost.
              </Text>
            </div>
            <InfoTip
              title="Licensed and unlicensed, side by side"
              content={{
                what: 'For each department: how much its Copilot seats are used, and how much Copilot the people without seats are doing anyway.',
                how: `Both "interactions per user" columns are normalised to a ${o.habitBucketNormalisationDays}-day month. The licensed one divides by all seats held, including idle ones - that is deliberate, because an idle seat is exactly what the comparison is meant to surface. The unlicensed one divides by people who were actually active, since there is no such thing as an idle non-licence. Departments with fewer than ${o.minSeatsPerSegment} of either population are omitted.`,
                source:
                  'The shading marks the outliers in each column. Look for a department where the right-hand number beats the left-hand one.',
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <CombinedSegmentTable rows={summary.combinedByDepartment} />
          </div>
        </Card>
      )}

      {summary.topResourceTypes.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                What Copilot is working on
              </Text>
              <Text size={200} block className={styles.muted}>
                The kinds of tenant content Copilot grounded its answers in.
              </Text>
            </div>
            <div className={styles.cardTools}>
              <InfoTip
                title="What Copilot is working on"
                content={{
                  what: 'The types of organisational content (documents, meetings, chats and so on) that Copilot actually referenced when answering.',
                  how: `Counted from the resources recorded against each Copilot interaction in the audit log, top ${o.topSegments} types. One interaction can reference several resources, so this counts references rather than interactions.`,
                  source:
                    'The clearest available evidence that Copilot is doing work on your own data rather than answering generic questions any free chatbot could. A population whose Copilot use never touches tenant content is getting little that a seat pays for.',
                }}
              />
              {sql?.resourceTypes && <SqlPopover sql={sql.resourceTypes} title="SQL behind this chart" />}
            </div>
          </div>
          <div className={styles.cardBody}>
            <CategoryBarChart categories={summary.topResourceTypes} valueLabel="References" />
          </div>
        </Card>
      )}

      {summary.intensityByDepartment.length > 0 && (
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                Usage frequency and intensity
              </Text>
              <Text size={200} block className={styles.muted}>
                Two departments on the same adoption rate can sit in opposite corners of this chart, and need
                opposite interventions.
              </Text>
            </div>
            <InfoTip
              title="Usage frequency and intensity"
              content={{
                what: 'Each department plotted by how often its users open Copilot (horizontal) against how much they do each time (vertical), with the bubble sized by seats held and coloured by average engagement.',
                how: `Only users who were active at least once are averaged, so unused seats do not drag a department towards the origin - they are counted in the reclaim figures instead. Active days are normalised to a ${o.habitBucketNormalisationDays}-day month so the axis does not change meaning with the period. Departments with fewer than ${o.minSeatsPerSegment} seats are omitted.`,
                formula:
                  `x = mean(activeDays of active users) x ${o.habitBucketNormalisationDays} / ${o.windowDays}\n` +
                  'y = sum(interactions of active users) / sum(activeDays of active users)',
                source:
                  'Bottom-right is frequent but shallow - those users need richer scenarios. Top-left is deep but occasional - those users need a reason to come back tomorrow.',
              }}
            />
          </div>
          <div className={styles.cardBody}>
            <IntensityScatter points={summary.intensityByDepartment} options={o} />
          </div>
        </Card>
      )}

      <Card>
        <div className={styles.cardHead}>
          <div>
            <Text weight="semibold" size={400}>
              Adoption by department
            </Text>
            <Text size={200} block className={styles.muted}>
              Lowest adoption first - the running order for an enablement plan. Departments with fewer than{' '}
              {o.minSeatsPerSegment} seats are omitted because the percentage would not be meaningful.
            </Text>
          </div>
          <InfoTip
            title="Adoption by department"
            content={{
              what: 'Copilot adoption for each department, worst first, with the raw seat counts alongside the percentage.',
              how: `Department comes from the imported user metadata; users with none are grouped as "(no department)". A department needs at least ${o.minSeatsPerSegment} seats to appear - a two-seat department with one active user is a 50% data point that means nothing and would sit at the top of the list.`,
              source:
                'The counts are shown next to the rate deliberately: 0% across six seats and 0% across six hundred are the same percentage and completely different decisions.',
            }}
          />
        </div>
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
            The same measures as the department table, for organisations that run enablement regionally.
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
            <div className={styles.cardTools}>
              <InfoTip
                title="Where the unmet demand is"
                content={{
                  what: `How many unlicensed users in each department scored ${o.opportunityRecommendScore} or above on the business-case score - i.e. how many people there have a strong case for a seat they do not have.`,
                  how: 'Only recommended candidates are counted, not every unlicensed user. Disabled accounts are excluded. The full ranked list with each person\u2019s justification is on the "Licence opportunities" tab.',
                  source:
                    'Read against the department adoption table: a department that appears in both has seats going unused and people who would use them, which is a reassignment rather than a purchase.',
                }}
              />
              {sql?.licenceOpportunities && (
                <SqlPopover sql={sql.licenceOpportunities} title="SQL behind this chart" />
              )}
            </div>
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
  const weights = [o.frequencyWeight, o.depthWeight, o.breadthWeight];
  const weightSum = weights.reduce((total, w) => total + w, 0);
  const frequencyTargetDays = Math.round(o.windowDays * (o.workingDaysPerWeek / 7) * o.frequencyTargetRatio);

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
                <strong>Frequency ({formatPct(weightSharePct(o.frequencyWeight, weights))} of the score).</strong>{' '}
                How many distinct days they used Copilot, against a target of{' '}
                {formatPct(o.frequencyTargetRatio * 100)} of the working days available in the period
                (assuming {o.workingDaysPerWeek} working days a week). Over a {o.windowDays}-day period that
                target is <strong>{frequencyTargetDays} active days</strong>. Working days rather than calendar
                days: measured against calendar days, someone who used Copilot every single working day would
                cap out at about 71% and look like a partial adopter.
              </Text>
              <Text>
                <strong>Depth ({formatPct(weightSharePct(o.depthWeight, weights))}).</strong> Interactions per
                active day, against a target of {o.depthTargetInteractionsPerActiveDay} per day. This is what
                separates "opened it once that day" from "worked with it that day", and it is measured per{' '}
                <em>active</em> day so that someone who uses Copilot intensively twice a week is not penalised
                twice for the same low frequency.
              </Text>
              <Text>
                <strong>Breadth ({formatPct(weightSharePct(o.breadthWeight, weights))}).</strong> How many
                distinct Copilot surfaces (Teams, Word, Outlook, Copilot Chat and so on) they use, against a
                target of {o.breadthTargetApps}. Users who only ever use one surface are the cheapest group to
                move, because they have already accepted Copilot - they simply have not been shown where else
                it works.
              </Text>
              <Text>
                Each component is a ratio capped at 1 before it is weighted, so nothing above target buys extra
                credit and no single component can carry a user on its own. The weighted sum is divided by the
                total of the three weights, which is what keeps the result on a 0-100 scale whatever the
                weights are set to:
              </Text>
              <div className={styles.formula}>
                {`frequency = min(1, activeDays / expectedActiveDays)\n` +
                  `depth     = min(1, (interactions / activeDays) / ${o.depthTargetInteractionsPerActiveDay})\n` +
                  `breadth   = min(1, appsUsed / ${o.breadthTargetApps})\n\n` +
                  `score = (frequency x ${o.frequencyWeight} + depth x ${o.depthWeight} + breadth x ${o.breadthWeight})\n` +
                  `        / ${weightSum} x 100`}
              </div>
              <Text>
                <strong>Worked example.</strong> Over a {o.windowDays}-day period the frequency target is{' '}
                {frequencyTargetDays} active days. A user active on half of those, averaging{' '}
                {o.depthTargetInteractionsPerActiveDay} interactions on each of those days, in a single app,
                scores{' '}
                {Math.round(
                  ((0.5 * o.frequencyWeight + 1 * o.depthWeight + (1 / o.breadthTargetApps) * o.breadthWeight) /
                    (weightSum || 1)) *
                    100,
                )}{' '}
                - deep but narrow and intermittent, which is why the recommended action for that profile is to
                broaden rather than to train.
              </Text>
            </div>
          </AccordionPanel>
        </AccordionItem>

        <AccordionItem value="bands">
          <AccordionHeader>What the engagement bands and habit buckets mean</AccordionHeader>
          <AccordionPanel>
            <div className={styles.method}>
              <Text>
                <strong>Bands</strong> turn the score into a decision. Champion at {o.championScore} and above,
                Established at {o.establishedScore}+, Developing at {o.developingScore}+, and Trialling below
                that. <strong>Established and above is what "habitual users" counts</strong> - the point at which
                Copilot is a routine part of the working week rather than something the person has tried.
              </Text>
              <Text>
                Users with <em>no</em> activity in the period are never scored at all, because a score of zero
                would put two completely different problems in the same bucket. They are split into{' '}
                <em>Dormant</em> (used Copilot at some point in the last {o.historyDays} days, but not once in
                this period) and <em>Never used</em> (no Copilot activity anywhere in that history). The
                distinction decides the action: a dormant user tried Copilot and stopped, and needs a
                conversation about what went wrong before the seat is taken away; a never-used seat has produced
                nothing at all and needs either onboarding or reassignment. Together they are the{' '}
                <strong>reclaimable seats</strong> figure.
              </Text>
              <Text>
                <strong>Habit buckets</strong> answer the same question with no weighting in it, which is what
                makes them useful to a sceptical reader: how many days a month does this person actually open
                Copilot? Infrequent is 1-{o.habitModerateMinDays - 1} days, Moderate {o.habitModerateMinDays}-
                {o.habitFrequentMinDays - 1}, Frequent {o.habitFrequentMinDays}-{o.habitDailyMinDays - 1}, and
                Daily {o.habitDailyMinDays}+ - essentially every working day.
              </Text>
              <Text>
                Because the reporting period is adjustable, active days are restated as days per{' '}
                {o.habitBucketNormalisationDays}-day month, and rounded to whole days, before bucketing.
                Without the normalisation, "11+ active days" would mean a near-daily user over a 28-day period
                and a once-a-fortnight user over a 180-day one, and the tile would silently change meaning when
                the period was changed. The rounding is what lets the tile captions describe the comparison
                exactly rather than approximately:
              </Text>
              <div className={styles.formula}>
                {`daysPerMonth = round(activeDays x ${o.habitBucketNormalisationDays} / ${o.windowDays})`}
              </div>
              <Text>
                The habit percentages are a share of <em>active</em> users, not of all seats. Someone who never
                opened Copilot is not an infrequent user - they are a reclaimable seat, and merging the two would
                hide the more expensive of the two problems.
              </Text>
            </div>
          </AccordionPanel>
        </AccordionItem>

        <AccordionItem value="actions">
          <AccordionHeader>How the recommended action is chosen</AccordionHeader>
          <AccordionPanel>
            <div className={styles.method}>
              <Text>
                Every licensed user gets exactly one recommended action, so the counts on the enablement plan
                add up to the whole licensed population. The action follows from the band, and for the middle
                bands from the breadth score as well - a user with a genuine habit confined to one Copilot
                surface needs broadening, not more coaching.
              </Text>
              <ActionPlan actions={summary.actionPlan} />
              <Text className={styles.muted}>
                On screen each user carries a two-word tag and the meaning is stated once, above - the same
                sentence repeated down every row of a band is noise, not evidence. The CSV export keeps the full
                sentence on every row, because a spreadsheet gets sorted and filtered and cannot rely on a
                legend being nearby.
              </Text>
            </div>
          </AccordionPanel>
        </AccordionItem>

        <AccordionItem value="opportunity">
          <AccordionHeader>How licence candidates are ranked</AccordionHeader>          <AccordionPanel>
            <div className={styles.method}>
              <Text>
                Unlicensed users are scored out of 100 on four weighted signals, with the weighting set so that
                evidence beats inference. Anyone reaching {o.opportunityRecommendScore} is counted as a
                recommended candidate.
              </Text>
              <Text>
                <strong>
                  Already using Copilot Chat without a licence ({o.opportunityUnlicensedCopilotWeight} points).
                </strong>{' '}
                The heaviest signal by a wide margin. It is the only one that proves demand for Copilot itself
                rather than inferring it from general activity, and it is invisible in Microsoft's own reports,
                which cover licensed users only.
              </Text>
              <Text>
                <strong>Teams collaboration ({o.opportunityCollaborationWeight})</strong>,{' '}
                <strong>email volume ({o.opportunityEmailWeight})</strong> and{' '}
                <strong>document work ({o.opportunityDocumentWeight})</strong> make up the rest. They identify
                heavy knowledge workers who would benefit but have never had the chance to try it.
              </Text>
              <div className={styles.formula}>
                {`copilot   = min(1, unlicensedCopilotInteractions / ${o.opportunityCopilotTarget})\n` +
                  `collab    = min(1, (teamsMessages + teamsMeetings) / ${o.opportunityCollaborationTarget})\n` +
                  `email     = min(1, (emailsSent + emailsRead) / ${o.opportunityEmailTarget})\n` +
                  `documents = min(1, filesViewedOrEdited / ${o.opportunityDocumentTarget})\n\n` +
                  `score = copilot x ${o.opportunityUnlicensedCopilotWeight} + collab x ${o.opportunityCollaborationWeight}` +
                  ` + email x ${o.opportunityEmailWeight} + documents x ${o.opportunityDocumentWeight}`}
              </div>
              <Text>
                Each signal is capped at its target before weighting, which matters: without the cap a single
                extremely noisy mailbox would clear the threshold on email alone. As the weights stand, a user
                with no unlicensed Copilot use has to be heavy across{' '}
                {o.opportunityCollaborationWeight + o.opportunityEmailWeight + o.opportunityDocumentWeight >=
                o.opportunityRecommendScore
                  ? 'more than one'
                  : 'every'}{' '}
                Microsoft 365 workload to be recommended, whereas proven Copilot use plus one heavy workload
                gets there on its own.
              </Text>
              <Text>
                Disabled accounts are excluded from the candidate list. They are, however, kept in the licensed
                user list - a disabled account still holding a Copilot seat is the clearest reclaim there is.
              </Text>
            </div>
          </AccordionPanel>
        </AccordionItem>

        <AccordionItem value="agents">
          <AccordionHeader>How agents and unlicensed use are measured</AccordionHeader>
          <AccordionPanel>
            <div className={styles.method}>
              <Text>
                <strong>Agents.</strong> An agent appears here only once it has been invoked - the Copilot
                audit log records agents that were <em>used</em>, not agents that exist, so an agent that was
                built and never run is invisible to this tool and to everyone else. Agent figures are counted
                across the whole tenant, licensed and unlicensed: an agent's worth to the organisation does
                not depend on the licence status of the people using it.
              </Text>
              <Text>
                <strong>Agent verdicts.</strong> Retire at {o.agentRetireInactiveDays}+ days without use;
                Review between {o.agentReviewInactiveDays} and {o.agentRetireInactiveDays} days, or while
                still current but used by fewer than {o.agentMinUsers} people; Keep when used within{' '}
                {o.agentReviewInactiveDays} days by at least {o.agentMinUsers} people. Any agent first seen
                within the last {o.agentNewDays} days is marked <em>New</em> and exempted from review
                entirely - a brand-new agent with two users has not failed, it has not started, and retiring
                it on that evidence is how an agent programme gets strangled in its first month.
              </Text>
              <Text>
                The inventory deliberately covers the full {o.historyDays}-day history rather than the
                selected period. An agent nobody has touched for six months is exactly what an inventory
                review is looking for, and it would be invisible in a 28-day window.
              </Text>
              <Text>
                <strong>Unlicensed Copilot Chat</strong> is reported as a population in its own right, using
                identical habit rules to the licensed side so the two distributions can be read against each
                other. Its figures come from a separate query to the licence-candidate ranking: that one is
                capped and sorted by score, so its rows are a biased sample and must never be used to
                describe the shape of a population.
              </Text>
              <Text>
                <strong>Usage concentration</strong> ranks active licensed users by interaction count and
                cuts them into percentile cohorts. Only active users are ranked - including idle seats would
                place every one of them in the bottom cohort at zero and give every tenant an identical
                chart. Percentiles rather than fixed counts, so a 50-seat tenant and a 50,000-seat one are
                directly comparable.
              </Text>
            </div>
          </AccordionPanel>
        </AccordionItem>

        <AccordionItem value="sources">
          <AccordionHeader>Where the data comes from</AccordionHeader>          <AccordionPanel>
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
  const o = summary.options;
  const seatSkus = summary.seatLicenceTypes.filter((l) => l.isCopilotSeat);
  const scoreWeights = [o.frequencyWeight, o.depthWeight, o.breadthWeight];

  const items: KpiDefinition[] = [
    {
      key: 'licensed',
      label: 'Copilot licences',
      value: formatCount(summary.licensedUsers),
      hint: `${seatSkus.length} seat SKU(s) counted`,
      info: {
        what: 'People holding at least one licence that this tool classified as a Microsoft 365 Copilot seat. It is the denominator for every percentage on this page.',
        how: 'Counted from the imported licence assignments, de-duplicated per user - someone holding two Copilot SKUs counts once. Microsoft ships Copilot-branded SKUs that are not a Copilot seat (Copilot Studio, Copilot for Sales), so the classification is listed in full, with the ones that were excluded, under "How this is calculated".',
        source:
          'Needs the user metadata import. It is a licence-assignment count, not a purchase count, so unassigned seats you are paying for do not appear here.',
      },
    },
    {
      key: 'adoption',
      label: 'Adoption rate',
      value: formatPct(summary.adoptionRatePct),
      hint: `${formatCount(summary.activeUsers)} used Copilot in this period`,
      tone: summary.adoptionRatePct >= 70 ? 'good' : summary.adoptionRatePct >= 40 ? 'warning' : 'critical',
      info: {
        what: 'The share of licensed users who used Copilot at least once in the selected period.',
        how: `A deliberately low bar, and the weakest number on this page: one interaction in ${o.windowDays} days counts the same as fifty. It is here because it is the figure everyone else quotes, so it needs to be visible and comparable - but "habitual users" below is the one to act on.`,
        formula: `${formatCount(summary.activeUsers)} active / ${formatCount(
          summary.licensedUsers,
        )} licensed = ${formatPct(summary.adoptionRatePct)}`,
        source: `Activity comes from the Copilot audit log for the ${o.windowDays}-day period, falling back to Microsoft\u2019s per-user usage report where the audit import is unavailable.`,
      },
    },
    {
      key: 'habit',
      label: 'Habitual users',
      value: formatPct(summary.habitRatePct),
      hint: `${formatCount(summary.habitualUsers)} have made Copilot part of the working week`,
      tone: summary.habitRatePct >= 50 ? 'good' : summary.habitRatePct >= 25 ? 'warning' : 'critical',
      info: {
        what: 'Licensed users for whom Copilot is a routine part of the working week, rather than something they have merely touched.',
        how: `A user is habitual when their engagement score reaches ${o.establishedScore} out of 100 - the Established and Champion bands. Reaching that needs sustained use across most weeks, more than a single interaction per day, and normally more than one Copilot surface; no one component can get there alone.`,
        formula: `${formatCount(summary.habitualUsers)} users scoring >= ${o.establishedScore} / ${formatCount(
          summary.licensedUsers,
        )} licensed = ${formatPct(summary.habitRatePct)}`,
        source:
          'This is the figure that tracks realised value. Adoption rate can sit at 100% while this sits near zero, which is exactly the situation a renewal conversation needs to surface.',
      },
    },
    {
      key: 'reclaim',
      label: 'Reclaimable seats',
      value: formatCount(summary.reclaimableSeats),
      hint: `${formatCount(summary.neverUsedUsers)} never used, ${formatCount(summary.dormantUsers)} dormant`,
      tone: summary.reclaimableSeats > 0 ? 'critical' : 'good',
      info: {
        what: 'Seats held by someone who did nothing with Copilot in the entire period. The directly actionable cost figure on this page.',
        how: `Split in two because they need opposite responses. "Never used" means no Copilot activity anywhere in the last ${o.historyDays} days - that seat has produced nothing and needs onboarding or reassignment. "Dormant" means they used it before the period but not inside it - that is someone who tried it and stopped, and needs a conversation before the seat is taken away.`,
        formula: `${formatCount(summary.neverUsedUsers)} never used + ${formatCount(
          summary.dormantUsers,
        )} dormant = ${formatCount(summary.reclaimableSeats)}`,
        source:
          'Disabled accounts still holding a seat are included and are the clearest reclaim of all - filter for them on the "Licensed users" tab.',
      },
    },
    {
      key: 'score',
      label: 'Average engagement',
      value: Math.round(summary.averageAdoptionScore),
      hint: `Median ${Math.round(summary.medianAdoptionScore)} of 100`,
      info: {
        what: 'The mean engagement score across all licensed users, including everyone scoring zero.',
        how: `Each user's score out of 100 combines frequency (${formatPct(
          weightSharePct(o.frequencyWeight, scoreWeights),
        )}), depth (${formatPct(weightSharePct(o.depthWeight, scoreWeights))}) and breadth (${formatPct(
          weightSharePct(o.breadthWeight, scoreWeights),
        )}). Unused seats are included in the average on purpose - excluding them would make a tenant with half its seats idle look identical to one with none.`,
        formula: `mean = ${Math.round(summary.averageAdoptionScore)}, median = ${Math.round(
          summary.medianAdoptionScore,
        )}`,
        source:
          'The median is shown next to the mean because a handful of Champions pull the mean up. When the mean is well above the median, the population is a small group of heavy users plus a long tail - a different problem from uniformly light use.',
      },
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
      info: {
        what: 'Licensed users who used Microsoft 365 Copilot Cowork in the period, as a share of all licensed users.',
        how: 'Cowork interactions are identified from the agents recorded against each Copilot interaction in the audit log. A user counts once no matter how many Cowork interactions they had.',
        source:
          'This card only appears when Cowork activity was actually detected. On a tenant that has not enabled it, showing "0%" would read as a failure rather than as "not applicable here".',
      },
    });
  }

  if (summary.unlicensedActiveUsers > 0) {
    items.push({
      key: 'unlicensed',
      label: 'Using Copilot unlicensed',
      value: formatCount(summary.unlicensedActiveUsers),
      hint: 'Proven demand - already using Copilot Chat with no seat',
      tone: 'opportunity',
      info: {
        what: 'People with no Microsoft 365 Copilot seat who nevertheless used Copilot in the period - in practice, Copilot Chat, which is available without a seat.',
        how: 'Counted from the Copilot audit log for every user who holds none of the SKUs classified as a Copilot seat. It is a count of distinct people, not of interactions.',
        source:
          'This is invisible in Microsoft\u2019s own Copilot usage reports, which cover licensed users only. It is the strongest evidence of unmet demand available, because these people chose to use Copilot with no prompting and no seat.',
      },
    });
  }

  items.push({
    key: 'candidates',
    label: 'Recommended for a licence',
    value: formatCount(summary.recommendedForLicence),
    hint: 'Heavy Microsoft 365 users with a strong business case',
    tone: 'opportunity',
    info: {
      what: `Unlicensed users whose business-case score reached ${o.opportunityRecommendScore} out of 100.`,
      how: `Four weighted signals: already using Copilot Chat without a seat (${o.opportunityUnlicensedCopilotWeight} points, the heaviest because it is evidence rather than inference), Teams collaboration (${o.opportunityCollaborationWeight}), email volume (${o.opportunityEmailWeight}) and document work (${o.opportunityDocumentWeight}). Each is a capped ratio against its own target, so no single workload can carry someone over the threshold alone.`,
      formula: `recommended when score >= ${o.opportunityRecommendScore}`,
      source:
        'Disabled accounts are excluded. The Microsoft 365 activity signals come from the latest usage-report snapshot, which is a Microsoft-defined window rather than the period selected above - see the "Licence opportunities" tab for each candidate\u2019s justification.',
    },
  });

  return items;
}

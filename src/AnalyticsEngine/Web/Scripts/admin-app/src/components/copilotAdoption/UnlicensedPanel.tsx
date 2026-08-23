import { makeStyles, tokens, Text, Card } from '@fluentui/react-components';
import type { CopilotAdoptionOptions, UnlicensedPopulationSummary } from '../../types/copilotAdoption';
import TreemapChart from '../charts/TreemapChart';
import CategoryBarChart from '../charts/CategoryBarChart';
import SqlPopover from '../SqlPopover';
import InfoTip from './InfoTip';
import HabitStrip from './HabitStrip';
import { KpiGrid, formatCount } from './KpiGrid';
import type { KpiDefinition } from './KpiGrid';

const useStyles = makeStyles({
  stack: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
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
  cardBody: {
    marginTop: '10px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
});

/**
 * Unlicensed Copilot Chat as a population in its own right.
 *
 * Worth its own view rather than being folded into the licence-candidate list, because it answers a
 * different question. The candidate list asks "who should get a seat"; this asks "how much Copilot
 * is this organisation already doing without paying for it", which is both the strongest evidence of
 * demand available and the one Copilot population Microsoft's own reporting cannot see at all.
 */
export default function UnlicensedPanel({
  unlicensed,
  options,
  windowDays,
  sql,
}: {
  unlicensed: UnlicensedPopulationSummary;
  options: CopilotAdoptionOptions;
  windowDays: number;
  sql: Record<string, string> | null;
}) {
  const styles = useStyles();

  if (unlicensed.activeUsers === 0) {
    return (
      <Card>
        <Text weight="semibold" size={400}>
          No unlicensed Copilot use in this period
        </Text>
        <Text size={200} block className={styles.muted} style={{ marginTop: '6px' }}>
          Nobody without a Microsoft 365 Copilot seat used Copilot in the last {windowDays} days. That is
          either a genuine finding or a sign the Copilot audit import is not running - the Health page will
          say which.
        </Text>
      </Card>
    );
  }

  return (
    <div className={styles.stack}>
      <KpiGrid items={buildUnlicensedKpis(unlicensed, options, windowDays)} />

      {unlicensed.truncated && (
        <Text size={200} className={styles.muted}>
          The unlicensed population was capped at {formatCount(options.maxUnlicensedUsersScored)} users, so
          these figures are a floor rather than a total.
        </Text>
      )}

      <Card>
        <div className={styles.cardHead}>
          <div>
            <Text weight="semibold" size={400}>
              Habit formation without a licence
            </Text>
            <Text size={200} block className={styles.muted}>
              The same buckets the licensed population uses, so the two can be read against each other.
            </Text>
          </div>
          <InfoTip
            title="Habit formation without a licence"
            content={{
              what: 'People with no Copilot seat, split by how many days a month they use Copilot Chat.',
              how: `Identical rules to the licensed habit strip: active days in the period restated as days per ${options.habitBucketNormalisationDays}-day month and rounded to whole days. Infrequent 1-${options.habitModerateMinDays - 1}, Moderate ${options.habitModerateMinDays}-${options.habitFrequentMinDays - 1}, Frequent ${options.habitFrequentMinDays}-${options.habitDailyMinDays - 1}, Daily ${options.habitDailyMinDays}+.`,
              source:
                'Anyone in the Frequent or Daily tile has built a Copilot habit with no seat and no enablement. That is the strongest licence case in the building, and it is invisible in Microsoft\u2019s own reports.',
            }}
          />
        </div>
        <div className={styles.cardBody}>
          <HabitStrip buckets={unlicensed.habitBuckets} />
        </div>
      </Card>

      <div className={styles.twoUp}>
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                Where unlicensed Copilot is used
              </Text>
              <Text size={200} block className={styles.muted}>
                Worth comparing with the licensed breakdown - the two are usually not the same shape.
              </Text>
            </div>
            <div className={styles.cardTools}>
              <InfoTip
                title="Where unlicensed Copilot is used"
                content={{
                  what: 'Copilot interactions by unlicensed users, broken down by the app they happened in.',
                  how: `Top ${options.topSegments} app hosts from the Copilot audit log, for users holding none of the SKUs classified as a Copilot seat.`,
                  source:
                    'Unlicensed use concentrates in Copilot Chat and Teams, because that is what is available without a seat. Seats are normally sold on Word and Outlook - if the licensed breakdown looks the same as this one, the seats are not buying anything the free experience does not already give.',
                }}
              />
              {sql?.unlicensedUsageByApp && (
                <SqlPopover sql={sql.unlicensedUsageByApp} title="SQL behind this chart" />
              )}
            </div>
          </div>
          <div className={styles.cardBody}>
            {unlicensed.usageByApp.length > 0 ? (
              <TreemapChart categories={unlicensed.usageByApp} valueLabel="interactions" />
            ) : (
              <div className={styles.empty}>No per-app breakdown available.</div>
            )}
          </div>
        </Card>

        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                Unlicensed use by department
              </Text>
              <Text size={200} block className={styles.muted}>
                Where the unmet demand actually sits.
              </Text>
            </div>
            <div className={styles.cardTools}>
              <InfoTip
                title="Unlicensed use by department"
                content={{
                  what: 'Copilot interactions by unlicensed users, grouped by their department.',
                  how: `Top ${options.topSegments} departments by interaction volume. Counts interactions rather than people, so one very heavy user can lift a department.`,
                  source:
                    'Compare with the licensed/unlicensed table on the Overview tab: a department that appears here and also has idle seats can usually be rebalanced instead of bought for.',
                }}
              />
              {sql?.unlicensedUsage && <SqlPopover sql={sql.unlicensedUsage} title="SQL behind this chart" />}
            </div>
          </div>
          <div className={styles.cardBody}>
            {unlicensed.usageByDepartment.length > 0 ? (
              <CategoryBarChart categories={unlicensed.usageByDepartment} valueLabel="Interactions" />
            ) : (
              <div className={styles.empty}>No department information available for these users.</div>
            )}
          </div>
        </Card>
      </div>
    </div>
  );
}

function buildUnlicensedKpis(
  u: UnlicensedPopulationSummary,
  o: CopilotAdoptionOptions,
  windowDays: number,
): KpiDefinition[] {
  const habitual = u.habitBuckets
    .filter((b) => b.label === 'Frequent' || b.label === 'Daily')
    .reduce((sum, b) => sum + b.users, 0);

  return [
    {
      key: 'users',
      label: 'Unlicensed Copilot users',
      value: formatCount(u.activeUsers),
      tone: 'opportunity',
      hint: `Used Copilot in the last ${windowDays} days with no seat`,
      info: {
        what: 'Distinct people with no Microsoft 365 Copilot licence who nevertheless used Copilot in the period - in practice Copilot Chat, which is available without a seat.',
        how: 'Counted from the Copilot audit log for every user holding none of the SKUs classified as a Copilot seat. Distinct people, not interactions.',
        source:
          'Entirely invisible in Microsoft\u2019s own Copilot usage reports, which cover licensed users only. This is the single strongest piece of evidence for unmet demand available anywhere.',
      },
    },
    {
      key: 'interactions',
      label: 'Unlicensed interactions',
      value: formatCount(u.interactions),
      tone: 'opportunity',
      hint: `${u.interactionsPerUserPerMonth} per user per month`,
      info: {
        what: 'Total Copilot interactions run by people without a seat, and the monthly average per person.',
        how: `Interactions in the last ${windowDays} days, restated per ${o.habitBucketNormalisationDays}-day month so the per-user figure does not change meaning when the period changes.`,
        source:
          'Compare the per-user figure against the licensed population: unlicensed users out-using licensed ones is a seat-allocation problem, not an adoption problem.',
      },
    },
    {
      key: 'habitual',
      label: 'Habitual without a licence',
      value: formatCount(habitual),
      tone: habitual > 0 ? 'critical' : 'neutral',
      hint: `${o.habitFrequentMinDays}+ active days a month`,
      info: {
        what: 'Unlicensed people who use Copilot Chat frequently or daily - they have built a habit with no seat, no training and no prompting.',
        how: `The Frequent (${o.habitFrequentMinDays}-${o.habitDailyMinDays - 1} active days a month) and Daily (${o.habitDailyMinDays}+) buckets combined.`,
        source:
          'Coloured as a problem deliberately. Every person in this figure is doing knowledge work with a free tool that a seat would materially improve, and is a stronger candidate than anyone identified by inference from Teams or email volume.',
      },
    },
    {
      key: 'agents',
      label: 'Using agents unlicensed',
      value: formatCount(u.agentUsers),
      tone: 'opportunity',
      hint: 'Reached for an agent without a seat',
      info: {
        what: 'Unlicensed people who invoked at least one Copilot agent in the period.',
        how: 'Counted from the agent attributed to each Copilot interaction in the audit log.',
        source:
          'Agent use is a step beyond ad-hoc chat - someone going out of their way to use an agent has found a specific job for Copilot, which is a more concrete business case than volume alone.',
      },
    },
  ];
}

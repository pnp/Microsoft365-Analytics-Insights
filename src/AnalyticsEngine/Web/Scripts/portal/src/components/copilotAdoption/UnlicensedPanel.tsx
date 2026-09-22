import { makeStyles, tokens, Text, Card } from '@fluentui/react-components';
import type { CopilotAdoptionOptions, UnlicensedPopulationSummary } from '../../types/copilotAdoption';
import TreemapChart from '../charts/TreemapChart';
import CategoryBarChart from '../charts/CategoryBarChart';
import SqlPopover from '../SqlPopover';
import InfoTip from '../shared/InfoTip';
import HabitStrip from './HabitStrip';
import { KpiGrid, formatCount } from '../shared/KpiGrid';
import type { KpiDefinition } from '../shared/KpiGrid';
import { useT, type TFunction } from '../../i18n';

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
 * different question. The candidate list asks "who should get a licence"; this asks "how much Copilot
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
  const t = useT();

  if (unlicensed.activeUsers === 0) {
    return (
      <Card>
        <Text weight="semibold" size={400}>
          {t('copilotAdoptionUsers.unlicensed.emptyTitle')}
        </Text>
        <Text size={200} block className={styles.muted} style={{ marginTop: '6px' }}>
          {t('copilotAdoptionUsers.unlicensed.emptyBody', { days: windowDays })}
        </Text>
      </Card>
    );
  }

  return (
    <div className={styles.stack}>
      <KpiGrid items={buildUnlicensedKpis(t, unlicensed, options, windowDays)} />

      {unlicensed.truncated && (
        <Text size={200} className={styles.muted}>
          {t('copilotAdoptionUsers.unlicensed.truncated', { count: formatCount(options.maxUnlicensedUsersScored) })}
        </Text>
      )}

      <Card>
        <div className={styles.cardHead}>
          <div>
            <Text weight="semibold" size={400}>
              {t('copilotAdoptionUsers.unlicensed.habitTitle')}
            </Text>
            <Text size={200} block className={styles.muted}>
              {t('copilotAdoptionUsers.unlicensed.habitSubtitle')}
            </Text>
          </div>
          <InfoTip
            title={t('copilotAdoptionUsers.unlicensed.habitTitle')}
            content={{
              what: t('copilotAdoptionUsers.unlicensed.habitWhat'),
              how: t('copilotAdoptionUsers.unlicensed.habitHow', {
                days: options.habitBucketNormalisationDays,
                infrequentMax: options.habitModerateMinDays - 1,
                moderateMin: options.habitModerateMinDays,
                moderateMax: options.habitFrequentMinDays - 1,
                frequentMin: options.habitFrequentMinDays,
                frequentMax: options.habitDailyMinDays - 1,
                dailyMin: options.habitDailyMinDays,
              }),
              source: t('copilotAdoptionUsers.unlicensed.habitSource'),
            }}
          />
        </div>
        <div className={styles.cardBody}>
          <HabitStrip buckets={unlicensed.habitBuckets} options={options} />
        </div>
      </Card>

      <div className={styles.twoUp}>
        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                {t('copilotAdoptionUsers.unlicensed.whereUsedTitle')}
              </Text>
              <Text size={200} block className={styles.muted}>
                {t('copilotAdoptionUsers.unlicensed.whereUsedSubtitle')}
              </Text>
            </div>
            <div className={styles.cardTools}>
              <InfoTip
                title={t('copilotAdoptionUsers.unlicensed.whereUsedTitle')}
                content={{
                  what: t('copilotAdoptionUsers.unlicensed.whereUsedWhat'),
                  how: t('copilotAdoptionUsers.unlicensed.whereUsedHow', { topSegments: options.topSegments }),
                  source: t('copilotAdoptionUsers.unlicensed.whereUsedSource'),
                }}
              />
              {sql?.unlicensedUsageByApp && (
                <SqlPopover sql={sql.unlicensedUsageByApp} title={t('copilotAdoptionUsers.unlicensed.sqlBehindChart')} />
              )}
            </div>
          </div>
          <div className={styles.cardBody}>
            {unlicensed.usageByApp.length > 0 ? (
              <TreemapChart categories={unlicensed.usageByApp} valueLabel={t('copilotAdoptionUsers.unlicensed.interactionsValueLabel')} />
            ) : (
              <div className={styles.empty}>{t('copilotAdoptionUsers.unlicensed.noPerApp')}</div>
            )}
          </div>
        </Card>

        <Card>
          <div className={styles.cardHead}>
            <div>
              <Text weight="semibold" size={400}>
                {t('copilotAdoptionUsers.unlicensed.byDepartmentTitle')}
              </Text>
              <Text size={200} block className={styles.muted}>
                {t('copilotAdoptionUsers.unlicensed.byDepartmentSubtitle')}
              </Text>
            </div>
            <div className={styles.cardTools}>
              <InfoTip
                title={t('copilotAdoptionUsers.unlicensed.byDepartmentTitle')}
                content={{
                  what: t('copilotAdoptionUsers.unlicensed.byDepartmentWhat'),
                  how: t('copilotAdoptionUsers.unlicensed.byDepartmentHow', { topSegments: options.topSegments }),
                  source: t('copilotAdoptionUsers.unlicensed.byDepartmentSource'),
                }}
              />
              {sql?.unlicensedUsage && <SqlPopover sql={sql.unlicensedUsage} title={t('copilotAdoptionUsers.unlicensed.sqlBehindChart')} />}
            </div>
          </div>
          <div className={styles.cardBody}>
            {unlicensed.usageByDepartment.length > 0 ? (
              <CategoryBarChart categories={unlicensed.usageByDepartment} valueLabel={t('copilotAdoptionUsers.unlicensed.interactionsHeader')} />
            ) : (
              <div className={styles.empty}>{t('copilotAdoptionUsers.unlicensed.noDepartment')}</div>
            )}
          </div>
        </Card>
      </div>
    </div>
  );
}

function buildUnlicensedKpis(
  t: TFunction,
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
      label: t('copilotAdoptionUsers.unlicensed.usersLabel'),
      value: formatCount(u.activeUsers),
      tone: 'opportunity',
      hint: t('copilotAdoptionUsers.unlicensed.usersHint', { days: windowDays }),
      info: {
        what: t('copilotAdoptionUsers.unlicensed.usersWhat'),
        how: t('copilotAdoptionUsers.unlicensed.usersHow'),
        source: t('copilotAdoptionUsers.unlicensed.usersSource'),
      },
    },
    {
      key: 'interactions',
      label: t('copilotAdoptionUsers.unlicensed.interactionsLabel'),
      value: formatCount(u.interactions),
      tone: 'opportunity',
      hint: t('copilotAdoptionUsers.unlicensed.interactionsHint', { value: u.interactionsPerUserPerMonth }),
      info: {
        what: t('copilotAdoptionUsers.unlicensed.interactionsWhat'),
        how: t('copilotAdoptionUsers.unlicensed.interactionsHow', {
          windowDays,
          normalisationDays: o.habitBucketNormalisationDays,
        }),
        source: t('copilotAdoptionUsers.unlicensed.interactionsSource'),
      },
    },
    {
      key: 'habitual',
      label: t('copilotAdoptionUsers.unlicensed.habitualLabel'),
      value: formatCount(habitual),
      tone: habitual > 0 ? 'critical' : 'neutral',
      hint: t('copilotAdoptionUsers.unlicensed.habitualHint', { days: o.habitFrequentMinDays }),
      info: {
        what: t('copilotAdoptionUsers.unlicensed.habitualWhat'),
        how: t('copilotAdoptionUsers.unlicensed.habitualHow', {
          frequentMin: o.habitFrequentMinDays,
          frequentMax: o.habitDailyMinDays - 1,
          dailyMin: o.habitDailyMinDays,
        }),
        source: t('copilotAdoptionUsers.unlicensed.habitualSource'),
      },
    },
    {
      key: 'agents',
      label: t('copilotAdoptionUsers.unlicensed.agentsLabel'),
      value: formatCount(u.agentUsers),
      tone: 'opportunity',
      hint: t('copilotAdoptionUsers.unlicensed.agentsHint'),
      info: {
        what: t('copilotAdoptionUsers.unlicensed.agentsWhat'),
        how: t('copilotAdoptionUsers.unlicensed.agentsHow'),
        source: t('copilotAdoptionUsers.unlicensed.agentsSource'),
      },
    },
  ];
}

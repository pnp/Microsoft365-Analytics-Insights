import { makeStyles, tokens, Text, Button, Badge } from '@fluentui/react-components';
import { ArrowRight16Regular, Options16Regular } from '@fluentui/react-icons';
import type { CopilotAdoptionOptions, CopilotAdoptionSummary } from '../../types/copilotAdoption';
import { formatCount } from '../shared/KpiGrid';
import { formatNumber, plural, useT } from '../../i18n';
import {
  COWORK_ASSUMPTION_KEYS,
  customisesAny,
  formatModelled,
  modelledRange,
  projectCoworkTimeSaved,
  type CoworkProjection,
  type TimeSavedAssumptionState,
} from './coworkTimeSaved';
import {
  AssumptionBadge,
  COWORK_ACTIVITY_COLOUR,
  COWORK_ACTIVITY_LABEL,
  COWORK_OBSERVED_COLOUR,
  EVIDENCE_GREEN,
  ModelledBadge,
  TIME_SAVED_COWORK_COLOUR,
  TimeSavedHeroFrame,
  formatAssumption,
  wholeShares,
  type HeroStat,
} from './timeSavedShared';

const useStyles = makeStyles({
  observed: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    marginTop: '10px',
    color: tokens.colorNeutralForeground2,
  },
  observedBadge: {
    color: tokens.colorNeutralForegroundOnBrand,
    backgroundColor: EVIDENCE_GREEN,
    whiteSpace: 'nowrap',
  },
  breakdown: {
    marginTop: '18px',
  },
  bar: {
    display: 'flex',
    width: '100%',
    height: '26px',
    borderRadius: tokens.borderRadiusMedium,
    overflow: 'hidden',
    marginTop: '6px',
    backgroundColor: tokens.colorNeutralBackground3,
    // The slices are the information; browsers drop background colours when printing by default.
    printColorAdjust: 'exact',
    WebkitPrintColorAdjust: 'exact',
  },
  slice: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: '#ffffff',
    fontSize: '12px',
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
    minWidth: '2px',
    borderRightWidth: '1px',
    borderRightStyle: 'solid',
    borderRightColor: '#ffffff',
    ':last-child': {
      borderRightWidth: '0',
    },
  },
  legend: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '6px 16px',
    marginTop: '8px',
  },
  legendItem: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
  },
  swatch: {
    width: '12px',
    height: '12px',
    borderRadius: '3px',
    display: 'inline-block',
    flexShrink: 0,
    printColorAdjust: 'exact',
    WebkitPrintColorAdjust: 'exact',
  },
});

/**
 * The Cowork tab's headline: the time Cowork could give back on top of what Microsoft 365 Copilot
 * already saves - the value of enabling Cowork, paid for in Copilot Credits.
 *
 * Leads with the people ready for Cowork now, because that is the decision this tab exists for: who
 * to put in the spending policy. Every Copilot seat holder is beside it as the ceiling, and says it
 * is one - it has people who are not ready yet hand Cowork the same share of their work, so it
 * overstates. When nobody is ready, the ceiling leads instead rather than the headline vanishing.
 *
 * Beneath the figure, where it would come from: the share of the hours each kind of work Cowork can
 * take on accounts for - the meetings people organise and attend, the email they send, their Teams
 * messages and documents - beside the Cowork tasks already running. That is the question the figure
 * is asked for: not only how much time, but where.
 *
 * There is deliberately no Microsoft 365 Copilot figure here. These people already hold a licence,
 * so the time Copilot saves them is the licence's: enabling Cowork does not unlock it, and quoting
 * it here would let Copilot's evidence stand behind a figure used to justify Copilot Credits. The
 * time a licence could give back sits on the Licence opportunities tab, beside the decision it sizes.
 * No study has measured Cowork either, so this headline is badged as an assumption as well as
 * modelled; the one real measurement on it - Cowork use already observed - carries its own badge.
 */
export default function CoworkTimeSavedHero({
  summary,
  options,
  timeSaved,
  onAdjust,
  onShowPeople,
}: {
  summary: CopilotAdoptionSummary;
  options: CopilotAdoptionOptions;
  timeSaved: TimeSavedAssumptionState;
  onAdjust: () => void;
  onShowPeople: () => void;
}) {
  const styles = useStyles();
  const t = useT();
  const { assumptions, customised } = timeSaved;

  const ready = projectCoworkTimeSaved(summary.coworkValueEstimate, assumptions, options);
  const full = projectCoworkTimeSaved(summary.coworkFullRolloutEstimate, assumptions, options);
  const headline = ready ?? full;
  if (!headline) return null;

  const figures = {
    taskMinutes: formatAssumption(assumptions.taskMinutes),
    percent: formatNumber(assumptions.conservativeRatio * 100, { maximumFractionDigits: 1 }),
  };
  const readyUsers = summary.coworkRecommendedForPolicy;
  const hours = (projection: CoworkProjection) =>
    modelledRange(t, formatCount(projection.hoursLow), formatCount(projection.hoursHigh));

  const stats: HeroStat[] = [];
  if (ready && full) {
    stats.push({
      key: 'ceiling',
      value: t('copilotAdoptionTimeSaved.hero.hoursValue', { range: hours(full) }),
      label: t(
        plural(
          full.cohortUsers,
          'copilotAdoptionCowork.timeSaved.hero.ceiling.label.one',
          'copilotAdoptionCowork.timeSaved.hero.ceiling.label.other',
        ),
        { users: formatCount(full.cohortUsers) },
      ),
      hint: t('copilotAdoptionCowork.timeSaved.hero.ceiling.hint'),
    });
  }
  stats.push(
    {
      key: 'perPerson',
      value: t('copilotAdoptionTimeSaved.stat.perPerson.value', {
        range: modelledRange(t, formatModelled(headline.minutesPerPersonDayLow), formatModelled(headline.minutesPerPersonDayHigh)),
      }),
      label: t(ready ? 'copilotAdoptionCowork.timeSaved.hero.perPerson.label' : 'copilotAdoptionCowork.timeSaved.hero.perPerson.labelAll'),
      hint: t('copilotAdoptionCowork.timeSaved.hero.perPerson.hint'),
    },
    {
      key: 'fte',
      value: t('copilotAdoptionTimeSaved.stat.fte.value', {
        range: modelledRange(t, formatModelled(headline.fteLow), formatModelled(headline.fteHigh)),
      }),
      label: t('copilotAdoptionTimeSaved.stat.fte.label'),
      hint: t('copilotAdoptionTimeSaved.stat.fte.hint', { hours: formatCount(headline.hoursPerFullTimeMonth) }),
    },
  );

  // Where the time would come from: every kind of work, then the tasks already running - the order
  // the calculator's rows and the Excel report use, so the three agree part for part.
  const segments = [
    ...headline.activities.map((a) => ({
      key: a.activity as string,
      label: t(COWORK_ACTIVITY_LABEL[a.activity]),
      colour: COWORK_ACTIVITY_COLOUR[a.activity],
      hours: a.displayHours,
      sharePct: a.sharePct,
    })),
    ...(headline.observedUsers > 0
      ? [
          {
            key: 'observed',
            label: t('copilotAdoptionCowork.timeSaved.activity.observedTasks'),
            colour: COWORK_OBSERVED_COLOUR,
            hours: headline.observedDisplayHours,
            sharePct: headline.observedSharePct,
          },
        ]
      : []),
  ];
  const shares = wholeShares(segments.map((s) => s.sharePct));

  const breakdown =
    headline.hoursHigh > 0 ? (
      <div className={styles.breakdown}>
        <Text size={200} weight="semibold">
          {t('copilotAdoptionCowork.timeSaved.hero.breakdownTitle')}
        </Text>
        <div
          className={styles.bar}
          role="img"
          aria-label={t('copilotAdoptionCowork.timeSaved.hero.breakdownAria', {
            parts: segments
              .map((s, i) => t('copilotAdoptionCowork.timeSaved.hero.breakdownPart', { activity: s.label, share: `${shares[i]}%` }))
              .join(', '),
          })}
        >
          {segments.map((s, i) =>
            s.sharePct > 0 ? (
              <div
                key={s.key}
                className={styles.slice}
                style={{ width: `${s.sharePct}%`, backgroundColor: s.colour }}
                title={t('copilotAdoptionCowork.timeSaved.hero.sliceTitle', {
                  activity: s.label,
                  hours: formatCount(s.hours),
                  share: `${shares[i]}%`,
                })}
              >
                {shares[i] >= 10 ? `${shares[i]}%` : ''}
              </div>
            ) : null,
          )}
        </div>
        <div className={styles.legend}>
          {segments.map((s, i) => (
            <div key={s.key} className={styles.legendItem}>
              <span className={styles.swatch} style={{ backgroundColor: s.colour }} aria-hidden="true" />
              <Text size={200}>
                {t('copilotAdoptionCowork.timeSaved.hero.legendEntry', {
                  activity: s.label,
                  hours: formatCount(s.hours),
                  share: `${shares[i]}%`,
                })}
              </Text>
            </div>
          ))}
        </div>
      </div>
    ) : undefined;

  return (
    <TimeSavedHeroFrame
      id="cowork-time-saved-headline"
      accent={TIME_SAVED_COWORK_COLOUR}
      eyebrow={t('copilotAdoptionCowork.timeSaved.hero.eyebrow')}
      badges={
        <>
          <ModelledBadge />
          <AssumptionBadge />
        </>
      }
      infoTitle={t('copilotAdoptionCowork.timeSaved.hero.infoTitle')}
      info={{
        what: t('copilotAdoptionCowork.timeSaved.hero.info.what'),
        how: t('copilotAdoptionCowork.timeSaved.hero.info.how'),
        formula: t('copilotAdoptionCowork.timeSaved.hero.info.formula', {
          ...figures,
          days: formatNumber(headline.workingDaysPerMonth, { maximumFractionDigits: 2 }),
          hoursPerDay: formatAssumption(assumptions.hoursPerDay),
        }),
        source: t('copilotAdoptionCowork.timeSaved.hero.info.source'),
      }}
      headline={t('copilotAdoptionTimeSaved.hero.hoursRange', { range: hours(headline) })}
      subline={
        ready
          ? t(
              plural(
                ready.cohortUsers,
                'copilotAdoptionCowork.timeSaved.hero.readyAdoption.one',
                'copilotAdoptionCowork.timeSaved.hero.readyAdoption.other',
              ),
              { users: formatCount(ready.cohortUsers) },
            )
          : t(
              plural(
                headline.cohortUsers,
                'copilotAdoptionCowork.timeSaved.hero.fullAdoption.one',
                'copilotAdoptionCowork.timeSaved.hero.fullAdoption.other',
              ),
              { users: formatCount(headline.cohortUsers) },
            )
      }
      caption={t(
        plural(
          headline.tasks,
          'copilotAdoptionCowork.timeSaved.hero.caption.one',
          'copilotAdoptionCowork.timeSaved.hero.caption.other',
        ),
        { tasks: formatCount(headline.tasks) },
      )}
      stats={stats}
      breakdown={breakdown}
      basis={t(
        customisesAny(customised, COWORK_ASSUMPTION_KEYS)
          ? 'copilotAdoptionCowork.timeSaved.hero.basisCustom'
          : 'copilotAdoptionCowork.timeSaved.hero.basisDefaults',
        { percent: figures.percent },
      )}
      actions={
        <>
          <Button appearance="primary" size="small" icon={<Options16Regular />} onClick={onAdjust}>
            {t('copilotAdoptionTimeSaved.hero.adjust')}
          </Button>
          {readyUsers > 0 && (
            <Button appearance="secondary" size="small" icon={<ArrowRight16Regular />} iconPosition="after" onClick={onShowPeople}>
              {t(
                plural(
                  readyUsers,
                  'copilotAdoptionCowork.timeSaved.hero.seePeople.one',
                  'copilotAdoptionCowork.timeSaved.hero.seePeople.other',
                ),
                { users: formatCount(readyUsers) },
              )}
            </Button>
          )}
        </>
      }
      footer={
        summary.coworkDetected && summary.coworkUsers > 0 ? (
          <div className={styles.observed}>
            <Badge className={styles.observedBadge} size="small">
              {t('copilotAdoptionCowork.basis.observed')}
            </Badge>
            <Text size={200}>
              {summary.coworkReportTotalTasks > 0
                ? t(
                    plural(
                      summary.coworkUsers,
                      'copilotAdoptionCowork.timeSaved.hero.observed.tasks.one',
                      'copilotAdoptionCowork.timeSaved.hero.observed.tasks.other',
                    ),
                    { users: formatCount(summary.coworkUsers), tasks: formatCount(summary.coworkReportTotalTasks) },
                  )
                : t(
                    plural(
                      summary.coworkUsers,
                      'copilotAdoptionCowork.timeSaved.hero.observed.users.one',
                      'copilotAdoptionCowork.timeSaved.hero.observed.users.other',
                    ),
                    { users: formatCount(summary.coworkUsers) },
                  )}
            </Text>
          </div>
        ) : undefined
      }
    />
  );
}

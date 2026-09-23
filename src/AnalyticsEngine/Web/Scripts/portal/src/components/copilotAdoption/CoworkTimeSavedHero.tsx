import { makeStyles, tokens, Text, Button, Badge } from '@fluentui/react-components';
import { ArrowRight16Regular, Options16Regular } from '@fluentui/react-icons';
import type { CopilotAdoptionOptions, CopilotAdoptionSummary } from '../../types/copilotAdoption';
import { formatCount } from '../shared/KpiGrid';
import { formatNumber, plural, useT, type TranslationKey } from '../../i18n';
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
  EVIDENCE_GREEN,
  ModelledBadge,
  TIME_SAVED_COWORK_COLOUR,
  TimeSavedHeroFrame,
  formatAssumption,
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
});

/** The label for the Cowork task rate's origin, shown wherever the rate is. */
export function taskRateBasisKey(basis: CoworkProjection['rateBasis']): TranslationKey {
  switch (basis) {
    case 'observed':
      return 'copilotAdoptionCowork.timeSaved.rateBasis.observed';
    case 'custom':
      return 'copilotAdoptionCowork.timeSaved.rateBasis.custom';
    default:
      return 'copilotAdoptionCowork.timeSaved.rateBasis.assumed';
  }
}

/**
 * The Cowork tab's headline: the time Cowork could give back on top of what Microsoft 365 Copilot
 * already saves - the value of enabling Cowork, paid for in Copilot Credits.
 *
 * Leads with the people ready for Cowork now, because that is the decision this tab exists for: who
 * to put in the spending policy. Every Copilot seat holder is beside it as the ceiling, and says it
 * is one - it projects the tenant's Cowork users' average onto people who are not ready yet, so it
 * overstates. When nobody is ready, the ceiling leads instead rather than the headline vanishing.
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
    rate: formatAssumption(headline.tasksPerPerson),
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

  const basis = `${t(
    customisesAny(customised, COWORK_ASSUMPTION_KEYS)
      ? 'copilotAdoptionCowork.timeSaved.hero.basisCustom'
      : 'copilotAdoptionCowork.timeSaved.hero.basisDefaults',
    figures,
  )} ${t(taskRateBasisKey(headline.rateBasis))}`;

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
        { tasks: formatCount(headline.tasks), minutes: figures.taskMinutes },
      )}
      stats={stats}
      basis={basis}
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

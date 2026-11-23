import { makeStyles, tokens, Text, Button, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { ArrowRight16Regular, Options16Regular } from '@fluentui/react-icons';
import type { CopilotAdoptionOptions, CopilotAdoptionSummary } from '../../types/copilotAdoption';
import { formatCount } from '../shared/KpiGrid';
import { formatList, formatNumber, plural, useT } from '../../i18n';
import {
  COWORK_ASSUMPTION_KEYS,
  customisesAny,
  formatModelled,
  modelledRange,
  projectCoworkTimeSaved,
  projectCoworkAdoption,
  type CoworkProjection,
  type TimeSavedAssumptionState,
} from './coworkTimeSaved';
import { resolveTimeSavedCohort, type TimeSavedCohort } from './timeSavedCohort';
import {
  AssumptionBadge,
  COWORK_ACTIVITY_COLOUR,
  COWORK_ACTIVITY_LABEL,
  CohortPicker,
  ModelledBadge,
  TIME_SAVED_COWORK_COLOUR,
  TimeSavedHeroFrame,
  formatAssumption,
  formatWholeShare,
  wholeShares,
  type HeroStat,
} from './timeSavedShared';
import { CoworkAdoptionUnavailable, CoworkEvidenceNote } from './CoworkEvidence';

const useStyles = makeStyles({
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
 * Leads with recommended prime candidates with no Cowork use observed in the period. The broader
 * scenario covers eligible assessed non-users, including people not yet ready. The full policy
 * audience serves a separate decision and does not determine either forecast.
 *
 * Beneath the figure, where it would come from: the share of the hours each kind of work Cowork can
 * take on accounts for - the meetings people organise and attend, the email they send, their Teams
 * messages and documents. Observed use and unestimated existing benefit have their own section.
 * That is the question the figure
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
  cohort,
  onCohortChange,
  onAdjust,
  onShowPeople,
  onShowAll,
}: {
  summary: CopilotAdoptionSummary;
  options: CopilotAdoptionOptions;
  timeSaved: TimeSavedAssumptionState;
  /** The reader's choice of who to model - see useTimeSavedCohorts. */
  cohort: TimeSavedCohort;
  onCohortChange: (cohort: TimeSavedCohort) => void;
  onAdjust: () => void;
  /** Opens the people section. Omitted for a reader without the See PII permission, who has no list to open. */
  onShowPeople?: () => void;
  /** Opens every seat holder represented by the all-users cohort. Omitted without See PII. */
  onShowAll?: () => void;
}) {
  const styles = useStyles();
  const t = useT();
  const { assumptions, customised } = timeSaved;

  const { ready, full } = projectCoworkAdoption(summary, assumptions, options);
  const withoutLicence = projectCoworkTimeSaved(summary.coworkWithoutLicenceEstimate, assumptions, options);
  const resolved = resolveTimeSavedCohort(cohort, ready, full, withoutLicence);
  const picker = full || withoutLicence ? (
    <CohortPicker
      value={resolved?.cohort ?? cohort}
      onChange={onCohortChange}
      options={[
        {
          value: 'recommended',
          label: summary.coworkNewAdoptionWithheld
            ? t('copilotAdoptionCowork.timeSaved.cohort.readyWithheld')
            : summary.coworkNewAdoptionEstimate == null
              ? t('copilotAdoptionCowork.timeSaved.cohort.readyUnavailable')
              : t('copilotAdoptionCowork.timeSaved.cohort.ready', { users: formatCount(ready?.cohortUsers ?? 0) }),
          disabled: !ready,
        },
        ...(full
          ? [{ value: 'all' as const, label: t('copilotAdoptionCowork.timeSaved.cohort.all', { users: formatCount(full.cohortUsers) }) }]
          : []),
        ...(withoutLicence
          ? [{
              value: 'withoutLicence' as const,
              label: t('copilotAdoptionCowork.timeSaved.cohort.withoutLicence', { users: formatCount(withoutLicence.cohortUsers) }),
            }]
          : []),
      ]}
    />
  ) : undefined;
  if (!resolved) return <CoworkAdoptionUnavailable summary={summary}>{picker}</CoworkAdoptionUnavailable>;
  const { projection: headline, cohort: shown, fallback } = resolved;
  const everyone = shown === 'all';
  // People without a Copilot seat: a what-if on top of a licence, said as one wherever it is quoted.
  const unlicensed = shown === 'withoutLicence';

  const figures = {
    percent: formatNumber(assumptions.conservativeRatio * 100, { maximumFractionDigits: 1 }),
  };
  const readyUsers = ready?.cohortUsers ?? 0;
  const hours = (projection: CoworkProjection) =>
    modelledRange(t, formatCount(projection.hoursLow), formatCount(projection.hoursHigh));

  const stats: HeroStat[] = [];
  if (!everyone && !unlicensed && full) {
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
  } else if (everyone && ready) {
    // Modelling everyone, where a rollout would start is the comparison that matters.
    stats.push({
      key: 'ready',
      value: t('copilotAdoptionTimeSaved.hero.hoursValue', { range: hours(ready) }),
      label: t(
        plural(
          ready.cohortUsers,
          'copilotAdoptionCowork.timeSaved.hero.ready.label.one',
          'copilotAdoptionCowork.timeSaved.hero.ready.label.other',
        ),
        { users: formatCount(ready.cohortUsers) },
      ),
      hint: t('copilotAdoptionCowork.timeSaved.hero.ready.hint'),
    });
  }
  stats.push(
    {
      key: 'perPerson',
      value: t('copilotAdoptionTimeSaved.stat.perPerson.value', {
        range: modelledRange(t, formatModelled(headline.minutesPerPersonDayLow), formatModelled(headline.minutesPerPersonDayHigh)),
      }),
      label: t(
        unlicensed
          ? 'copilotAdoptionCowork.timeSaved.hero.perPerson.labelWithoutLicence'
          : everyone
            ? 'copilotAdoptionCowork.timeSaved.hero.perPerson.labelAll'
            : 'copilotAdoptionCowork.timeSaved.hero.perPerson.label',
      ),
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

  // Where the time would come from: every kind of work, in the order the calculator's rows and the
  // Excel report use, so the three agree part for part.
  const segments = headline.activities.map((a) => ({
    key: a.activity as string,
    label: t(COWORK_ACTIVITY_LABEL[a.activity]),
    colour: COWORK_ACTIVITY_COLOUR[a.activity],
    hours: a.displayHours,
    sharePct: a.sharePct,
  }));
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
            parts: formatList(
              segments.map((s, i) => t('copilotAdoptionCowork.timeSaved.hero.breakdownPart', { activity: s.label, share: formatWholeShare(shares[i]) })),
            ),
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
                  share: formatWholeShare(shares[i]),
                })}
              >
                {shares[i] >= 10 ? formatWholeShare(shares[i]) : ''}
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
                  share: formatWholeShare(shares[i]),
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
      eyebrow={t(unlicensed
        ? 'copilotAdoptionCowork.timeSaved.hero.licensingEyebrow'
        : 'copilotAdoptionCowork.timeSaved.hero.eyebrow')}
      badges={
        <>
          <ModelledBadge />
          <AssumptionBadge />
        </>
      }
      infoTitle={t('copilotAdoptionCowork.timeSaved.hero.infoTitle')}
      info={{
        what: t(
          unlicensed
            ? 'copilotAdoptionCowork.timeSaved.hero.info.whatWithoutLicence'
            : everyone
              ? 'copilotAdoptionCowork.timeSaved.hero.info.whatAll'
              : 'copilotAdoptionCowork.timeSaved.hero.info.what',
        ),
        how: t('copilotAdoptionCowork.timeSaved.hero.info.how'),
        formula: t('copilotAdoptionCowork.timeSaved.hero.info.formula', {
          ...figures,
          days: formatNumber(headline.workingDaysPerMonth, { maximumFractionDigits: 2 }),
          hoursPerDay: formatAssumption(assumptions.hoursPerDay),
        }),
        source: t('copilotAdoptionCowork.timeSaved.hero.info.source'),
      }}
      picker={picker}
      headline={t('copilotAdoptionTimeSaved.hero.hoursRange', { range: hours(headline) })}
      subline={
        unlicensed
          ? t(
              plural(
                headline.cohortUsers,
                'copilotAdoptionCowork.timeSaved.hero.withoutLicenceAdoption.one',
                'copilotAdoptionCowork.timeSaved.hero.withoutLicenceAdoption.other',
              ),
              { users: formatCount(headline.cohortUsers) },
            )
          : everyone
          ? t(
              plural(
                headline.cohortUsers,
                'copilotAdoptionCowork.timeSaved.hero.fullAdoption.one',
                'copilotAdoptionCowork.timeSaved.hero.fullAdoption.other',
              ),
              { users: formatCount(headline.cohortUsers) },
            )
          : t(
              plural(
                headline.cohortUsers,
                'copilotAdoptionCowork.timeSaved.hero.readyAdoption.one',
                'copilotAdoptionCowork.timeSaved.hero.readyAdoption.other',
              ),
              { users: formatCount(headline.cohortUsers) },
            )
      }
      caption={t(
        plural(
          headline.projectedTasks,
          'copilotAdoptionCowork.timeSaved.hero.caption.one',
          'copilotAdoptionCowork.timeSaved.hero.caption.other',
        ),
        { pieces: formatCount(headline.projectedTasks) },
      )}
      notice={
        unlicensed ? (
          <MessageBar intent="info">
            <MessageBarBody>
              {summary.licenceAllCandidatesEstimate?.candidatesCapped
                ? t('copilotAdoptionCowork.timeSaved.hero.withoutLicenceNoticeCapped', {
                    cap: formatCount(options.maxOpportunityCandidates),
                  })
                : t('copilotAdoptionCowork.timeSaved.hero.withoutLicenceNotice')}
            </MessageBarBody>
          </MessageBar>
        ) : everyone ? (
          <MessageBar intent="info">
            <MessageBarBody>
              {t(summary.coworkNewAdoptionWithheld
                ? 'copilotAdoptionCowork.coverage.withheld'
                : fallback ? 'copilotAdoptionCowork.timeSaved.hero.noneReady' : 'copilotAdoptionCowork.timeSaved.hero.allNotice')}
            </MessageBarBody>
          </MessageBar>
        ) : undefined
      }
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
          {unlicensed ? null : everyone ? (
            onShowAll && (
              <Button appearance="secondary" size="small" icon={<ArrowRight16Regular />} iconPosition="after" onClick={onShowAll}>
              {t(
                plural(
                  headline.cohortUsers,
                  'copilotAdoptionCowork.timeSaved.hero.seeAll.one',
                  'copilotAdoptionCowork.timeSaved.hero.seeAll.other',
                ),
                { users: formatCount(headline.cohortUsers) },
              )}
              </Button>
            )
          ) : (
            readyUsers > 0 && onShowPeople && (
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
            )
          )}
        </>
      }
      footer={<CoworkEvidenceNote summary={summary} model={!unlicensed} />}
    />
  );
}

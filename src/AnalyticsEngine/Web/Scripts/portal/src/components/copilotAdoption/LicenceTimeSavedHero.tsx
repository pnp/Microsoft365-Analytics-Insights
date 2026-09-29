import { makeStyles, tokens, Text, Button, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { ArrowRight16Regular, Options16Regular } from '@fluentui/react-icons';
import type { CopilotAdoptionOptions, CopilotAdoptionSummary } from '../../types/copilotAdoption';
import { formatCount } from '../shared/KpiGrid';
import { formatNumber, plural, useT } from '../../i18n';
import {
  LICENCE_ASSUMPTION_KEYS,
  customisesAny,
  formatModelled,
  modelledRange,
  projectLicenceTimeSaved,
  type LicenceProjection,
  type TimeSavedAssumptionState,
} from './coworkTimeSaved';
import { resolveTimeSavedCohort, type TimeSavedCohort } from './timeSavedCohort';
import {
  CohortPicker,
  ModelledBadge,
  PublishedEvidenceBadge,
  TIME_SAVED_ACTIVITY_COLOUR,
  TIME_SAVED_ACTIVITY_LABEL,
  TimeSavedHeroFrame,
  formatAssumption,
  wholeShares,
  type HeroStat,
} from './timeSavedShared';

const useStyles = makeStyles({
  notices: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
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
    gap: '16px',
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
  },
});

/**
 * The Licence opportunities tab's headline: the time Microsoft 365 Copilot could give back to the
 * people recommended for a licence - the figure a licence purchase is justified with.
 *
 * This is where the Copilot minutes per meeting, email and document belong. They are evidenced by
 * published studies of Microsoft 365 Copilot, the largest of which randomised who received a licence -
 * so it measured exactly the decision this tab supports. Beside it, the recommended candidates already
 * using Copilot Chat: the strongest part of the case, because their demand is observed rather than
 * inferred, and the part Copilot Chat may already be giving them some of.
 *
 * The reader can model every licence candidate instead of the recommended ones. That is what sizes a
 * purchase on a tenant where nobody uses Microsoft 365 heavily enough to be recommended - there, it
 * leads on its own and says why - and beside it the recommended candidates show how much of it the
 * list stands behind.
 *
 * There is deliberately no equivalent figure for people who already hold a licence: no decision
 * hangs on it.
 */
export default function LicenceTimeSavedHero({
  summary,
  options,
  timeSaved,
  cohort,
  onCohortChange,
  onAdjust,
  onShowRecommended,
  onShowAll,
}: {
  summary: CopilotAdoptionSummary;
  options: CopilotAdoptionOptions;
  timeSaved: TimeSavedAssumptionState;
  /** The reader's choice of who to model - see useTimeSavedCohorts. */
  cohort: TimeSavedCohort;
  onCohortChange: (cohort: TimeSavedCohort) => void;
  onAdjust: () => void;
  onShowRecommended: () => void;
  onShowAll: () => void;
}) {
  const styles = useStyles();
  const t = useT();
  const { assumptions, customised } = timeSaved;

  const recommended = projectLicenceTimeSaved(summary.licenceOpportunityEstimate, assumptions, options);
  const all = projectLicenceTimeSaved(summary.licenceAllCandidatesEstimate, assumptions, options);
  const resolved = resolveTimeSavedCohort(cohort, recommended, all);
  if (!resolved) return null;
  const { projection: headline, cohort: shown, fallback } = resolved;
  const everyone = shown === 'all';
  const chatUsers = projectLicenceTimeSaved(summary.licenceChatUsersEstimate, assumptions, options);

  const figures = {
    meeting: formatAssumption(assumptions.meetingMinutes),
    email: formatAssumption(assumptions.emailMinutes),
    document: formatAssumption(assumptions.documentMinutes),
    percent: formatNumber(assumptions.conservativeRatio * 100, { maximumFractionDigits: 1 }),
  };
  const hours = (projection: LicenceProjection) =>
    modelledRange(t, formatCount(projection.hoursLow), formatCount(projection.hoursHigh));

  const stats: HeroStat[] = [];
  if (!everyone) {
    stats.push(
      chatUsers
        ? {
            key: 'chatUsers',
            value: t('copilotAdoptionTimeSaved.hero.hoursValue', { range: hours(chatUsers) }),
            label: t(
              plural(
                chatUsers.cohortUsers,
                'copilotAdoptionTimeSaved.licence.hero.chatUsers.label.one',
                'copilotAdoptionTimeSaved.licence.hero.chatUsers.label.other',
              ),
              { users: formatCount(chatUsers.cohortUsers) },
            ),
            hint: t('copilotAdoptionTimeSaved.licence.hero.chatUsers.hint'),
          }
        : {
            key: 'chatUsers',
            value: '\u2014',
            label: t('copilotAdoptionTimeSaved.licence.hero.chatUsers.none'),
          },
    );
  } else if (recommended) {
    // Modelling everyone, the part of it the list stands behind is the comparison that matters.
    stats.push({
      key: 'recommended',
      value: t('copilotAdoptionTimeSaved.hero.hoursValue', { range: hours(recommended) }),
      label: t(
        plural(
          recommended.cohortUsers,
          'copilotAdoptionTimeSaved.licence.hero.recommended.label.one',
          'copilotAdoptionTimeSaved.licence.hero.recommended.label.other',
        ),
        { users: formatCount(recommended.cohortUsers) },
      ),
      hint: t('copilotAdoptionTimeSaved.licence.hero.recommended.hint'),
    });
  }
  stats.push(
    {
      key: 'perPerson',
      value: t('copilotAdoptionTimeSaved.stat.perPerson.value', {
        range: modelledRange(t, formatModelled(headline.minutesPerPersonDayLow), formatModelled(headline.minutesPerPersonDayHigh)),
      }),
      label: t(everyone ? 'copilotAdoptionTimeSaved.licence.hero.perPerson.labelAll' : 'copilotAdoptionTimeSaved.licence.hero.perPerson.label'),
      hint: t('copilotAdoptionTimeSaved.licence.hero.perPerson.hint'),
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

  const segments = headline.activities.map((a) => ({
    key: a.activity,
    label: t(TIME_SAVED_ACTIVITY_LABEL[a.activity]),
    colour: TIME_SAVED_ACTIVITY_COLOUR[a.activity],
    hours: a.displayHours,
    sharePct: a.sharePct,
  }));
  const shares = wholeShares(segments.map((s) => s.sharePct));

  const breakdown =
    headline.hoursHigh > 0 ? (
      <div className={styles.breakdown}>
        <Text size={200} weight="semibold">
          {t('copilotAdoptionTimeSaved.licence.hero.breakdownTitle')}
        </Text>
        <div
          className={styles.bar}
          role="img"
          aria-label={t('copilotAdoptionTimeSaved.licence.hero.breakdownAria', {
            meetings: `${shares[0]}%`,
            email: `${shares[1]}%`,
            documents: `${shares[2]}%`,
          })}
        >
          {segments.map((s, i) =>
            s.sharePct > 0 ? (
              <div
                key={s.key}
                className={styles.slice}
                style={{ width: `${s.sharePct}%`, backgroundColor: s.colour }}
                title={t('copilotAdoptionTimeSaved.licence.hero.sliceTitle', {
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
                {t('copilotAdoptionTimeSaved.licence.hero.legendEntry', {
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
      id="licence-time-saved-headline"
      accent={tokens.colorBrandStroke1}
      eyebrow={t('copilotAdoptionTimeSaved.licence.hero.eyebrow')}
      badges={
        <>
          <ModelledBadge />
          <PublishedEvidenceBadge />
        </>
      }
      infoTitle={t('copilotAdoptionTimeSaved.licence.hero.infoTitle')}
      info={{
        what: t(everyone ? 'copilotAdoptionTimeSaved.licence.hero.info.whatAll' : 'copilotAdoptionTimeSaved.licence.hero.info.what'),
        how: t(everyone ? 'copilotAdoptionTimeSaved.licence.hero.info.howAll' : 'copilotAdoptionTimeSaved.licence.hero.info.how'),
        formula: t('copilotAdoptionTimeSaved.licence.hero.info.formula', {
          ...figures,
          days: formatNumber(headline.workingDaysPerMonth, { maximumFractionDigits: 2 }),
          hoursPerDay: formatAssumption(assumptions.hoursPerDay),
        }),
        source: t('copilotAdoptionTimeSaved.licence.hero.info.source'),
      }}
      picker={
        all ? (
          <CohortPicker
            value={shown}
            onChange={onCohortChange}
            options={[
              {
                value: 'recommended',
                label: t('copilotAdoptionTimeSaved.cohort.licence.recommended', {
                  users: formatCount(recommended?.cohortUsers ?? 0),
                }),
                disabled: !recommended,
              },
              {
                value: 'all',
                label: t('copilotAdoptionTimeSaved.cohort.licence.all', { users: formatCount(all.cohortUsers) }),
              },
            ]}
          />
        ) : undefined
      }
      headline={t('copilotAdoptionTimeSaved.hero.hoursRange', { range: hours(headline) })}
      subline={
        everyone
          ? t(
              plural(
                headline.cohortUsers,
                'copilotAdoptionTimeSaved.licence.hero.cohortAll.one',
                'copilotAdoptionTimeSaved.licence.hero.cohortAll.other',
              ),
              { users: formatCount(headline.cohortUsers) },
            )
          : t(
              plural(
                headline.cohortUsers,
                'copilotAdoptionTimeSaved.licence.hero.cohort.one',
                'copilotAdoptionTimeSaved.licence.hero.cohort.other',
              ),
              { users: formatCount(headline.cohortUsers) },
            )
      }
      caption={t('copilotAdoptionTimeSaved.licence.hero.caption')}
      notice={
        everyone || headline.candidatesCapped ? (
          <div className={styles.notices}>
            {everyone && (
              <MessageBar intent="info">
                <MessageBarBody>
                  {t(
                    fallback
                      ? 'copilotAdoptionTimeSaved.licence.hero.noneRecommended'
                      : 'copilotAdoptionTimeSaved.licence.hero.allNotice',
                  )}
                </MessageBarBody>
              </MessageBar>
            )}
            {headline.candidatesCapped && (
              <MessageBar intent="warning">
                <MessageBarBody>
                  {t('copilotAdoptionTimeSaved.licence.hero.capped', { cap: formatNumber(options.maxOpportunityCandidates) })}
                </MessageBarBody>
              </MessageBar>
            )}
          </div>
        ) : undefined
      }
      stats={stats}
      breakdown={breakdown}
      basis={t(
        customisesAny(customised, LICENCE_ASSUMPTION_KEYS)
          ? 'copilotAdoptionTimeSaved.licence.hero.basisCustom'
          : 'copilotAdoptionTimeSaved.licence.hero.basisDefaults',
        figures,
      )}
      actions={
        <>
          <Button appearance="primary" size="small" icon={<Options16Regular />} onClick={onAdjust}>
            {t('copilotAdoptionTimeSaved.hero.adjust')}
          </Button>
          {everyone ? (
            <Button appearance="secondary" size="small" icon={<ArrowRight16Regular />} iconPosition="after" onClick={onShowAll}>
              {t(
                plural(
                  headline.cohortUsers,
                  'copilotAdoptionTimeSaved.licence.hero.seeAll.one',
                  'copilotAdoptionTimeSaved.licence.hero.seeAll.other',
                ),
                { users: formatCount(headline.cohortUsers) },
              )}
            </Button>
          ) : (
            summary.recommendedForLicence > 0 && (
              <Button appearance="secondary" size="small" icon={<ArrowRight16Regular />} iconPosition="after" onClick={onShowRecommended}>
                {t(
                  plural(
                    summary.recommendedForLicence,
                    'copilotAdoptionTimeSaved.licence.hero.seeRecommended.one',
                    'copilotAdoptionTimeSaved.licence.hero.seeRecommended.other',
                  ),
                  { users: formatCount(summary.recommendedForLicence) },
                )}
              </Button>
            )
          )}
        </>
      }
    />
  );
}

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
import {
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
 * There is deliberately no equivalent figure for people who already hold a licence: no decision
 * hangs on it.
 */
export default function LicenceTimeSavedHero({
  summary,
  options,
  timeSaved,
  onAdjust,
  onShowRecommended,
}: {
  summary: CopilotAdoptionSummary;
  options: CopilotAdoptionOptions;
  timeSaved: TimeSavedAssumptionState;
  onAdjust: () => void;
  onShowRecommended: () => void;
}) {
  const styles = useStyles();
  const t = useT();
  const { assumptions, customised } = timeSaved;

  const recommended = projectLicenceTimeSaved(summary.licenceOpportunityEstimate, assumptions, options);
  if (!recommended) return null;
  const chatUsers = projectLicenceTimeSaved(summary.licenceChatUsersEstimate, assumptions, options);

  const figures = {
    meeting: formatAssumption(assumptions.meetingMinutes),
    email: formatAssumption(assumptions.emailMinutes),
    document: formatAssumption(assumptions.documentMinutes),
    percent: formatNumber(assumptions.conservativeRatio * 100, { maximumFractionDigits: 1 }),
  };
  const hours = (projection: LicenceProjection) =>
    modelledRange(t, formatCount(projection.hoursLow), formatCount(projection.hoursHigh));

  const stats: HeroStat[] = [
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
    {
      key: 'perPerson',
      value: t('copilotAdoptionTimeSaved.stat.perPerson.value', {
        range: modelledRange(t, formatModelled(recommended.minutesPerPersonDayLow), formatModelled(recommended.minutesPerPersonDayHigh)),
      }),
      label: t('copilotAdoptionTimeSaved.licence.hero.perPerson.label'),
      hint: t('copilotAdoptionTimeSaved.licence.hero.perPerson.hint'),
    },
    {
      key: 'fte',
      value: t('copilotAdoptionTimeSaved.stat.fte.value', {
        range: modelledRange(t, formatModelled(recommended.fteLow), formatModelled(recommended.fteHigh)),
      }),
      label: t('copilotAdoptionTimeSaved.stat.fte.label'),
      hint: t('copilotAdoptionTimeSaved.stat.fte.hint', { hours: formatCount(recommended.hoursPerFullTimeMonth) }),
    },
  ];

  const segments = recommended.activities.map((a) => ({
    key: a.activity,
    label: t(TIME_SAVED_ACTIVITY_LABEL[a.activity]),
    colour: TIME_SAVED_ACTIVITY_COLOUR[a.activity],
    hours: a.displayHours,
    sharePct: a.sharePct,
  }));
  const shares = wholeShares(segments.map((s) => s.sharePct));

  const breakdown =
    recommended.hoursHigh > 0 ? (
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
        what: t('copilotAdoptionTimeSaved.licence.hero.info.what'),
        how: t('copilotAdoptionTimeSaved.licence.hero.info.how'),
        formula: t('copilotAdoptionTimeSaved.licence.hero.info.formula', {
          ...figures,
          days: formatNumber(recommended.workingDaysPerMonth, { maximumFractionDigits: 2 }),
          hoursPerDay: formatAssumption(assumptions.hoursPerDay),
        }),
        source: t('copilotAdoptionTimeSaved.licence.hero.info.source'),
      }}
      headline={t('copilotAdoptionTimeSaved.hero.hoursRange', { range: hours(recommended) })}
      subline={t(
        plural(
          recommended.cohortUsers,
          'copilotAdoptionTimeSaved.licence.hero.cohort.one',
          'copilotAdoptionTimeSaved.licence.hero.cohort.other',
        ),
        { users: formatCount(recommended.cohortUsers) },
      )}
      caption={t('copilotAdoptionTimeSaved.licence.hero.caption')}
      notice={
        recommended.candidatesCapped ? (
          <MessageBar intent="warning">
            <MessageBarBody>
              {t('copilotAdoptionTimeSaved.licence.hero.capped', { cap: formatNumber(options.maxOpportunityCandidates) })}
            </MessageBarBody>
          </MessageBar>
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
          {summary.recommendedForLicence > 0 && (
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
          )}
        </>
      }
    />
  );
}

import { makeStyles, tokens, Text, Button, Badge, Tooltip } from '@fluentui/react-components';
import { ArrowRight16Regular, Options16Regular } from '@fluentui/react-icons';
import type { CopilotAdoptionOptions, CopilotAdoptionSummary } from '../../types/copilotAdoption';
import { formatCount } from '../shared/KpiGrid';
import InfoTip from '../shared/InfoTip';
import { formatNumber, plural, useT, type TranslationKey } from '../../i18n';
import {
  formatModelled,
  modelledRange,
  projectTimeSaved,
  type CoworkTaskProjection,
  type TimeSavedActivity,
  type TimeSavedAssumptionState,
} from './coworkTimeSaved';

/** One hue per kind of work, used by the headline bar and the model's table so the two read as one. */
export const TIME_SAVED_ACTIVITY_COLOUR: Record<TimeSavedActivity, string> = {
  meetings: '#0f6cbd',
  email: '#8764b8',
  documents: '#038387',
};

/**
 * Cowork's hue - deliberately outside the Copilot family of blues, purple and teal, so the one layer
 * resting on an assumption rather than on published evidence is never mistaken for part of the others.
 */
export const TIME_SAVED_COWORK_COLOUR = '#c239b3';

export const TIME_SAVED_ACTIVITY_LABEL: Record<TimeSavedActivity, TranslationKey> = {
  meetings: 'copilotAdoptionCowork.timeSaved.activity.meetings',
  email: 'copilotAdoptionCowork.timeSaved.activity.email',
  documents: 'copilotAdoptionCowork.timeSaved.activity.documents',
};

const useStyles = makeStyles({
  hero: {
    position: 'relative',
    borderRadius: tokens.borderRadiusXLarge,
    padding: '22px 24px 18px',
    backgroundColor: tokens.colorNeutralBackground1,
    backgroundImage: `linear-gradient(120deg, ${tokens.colorBrandBackground2} 0%, ${tokens.colorNeutralBackground1} 62%)`,
    borderLeftWidth: '6px',
    borderLeftStyle: 'solid',
    borderLeftColor: tokens.colorBrandStroke1,
    boxShadow: tokens.shadow4,
    marginBottom: '16px',
    // Keeps the brand wash on paper: browsers drop background colours when printing by default, and
    // the headline without its panel reads as body text.
    printColorAdjust: 'exact',
    WebkitPrintColorAdjust: 'exact',
  },
  top: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
  },
  eyebrow: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    color: tokens.colorBrandForeground1,
    textTransform: 'uppercase',
    letterSpacing: '0.06em',
  },
  headline: {
    display: 'block',
    marginTop: '10px',
    fontSize: '44px',
    lineHeight: '52px',
    fontWeight: 700,
    color: tokens.colorBrandForeground1,
    fontVariantNumeric: 'tabular-nums',
    letterSpacing: '-0.01em',
  },
  subline: {
    display: 'block',
    marginTop: '2px',
    color: tokens.colorNeutralForeground2,
  },
  // The two layers, side by side under the headline: what Copilot gives back on the licences already
  // paid for, and what Cowork adds on top for Copilot Credits - each with the strength of its evidence.
  layers: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(280px, 1fr))',
    gap: '12px',
    marginTop: '16px',
  },
  layer: {
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderTopWidth: '4px',
    borderTopStyle: 'solid',
    borderRadius: tokens.borderRadiusMedium,
    padding: '12px 14px',
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  layerHead: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '8px',
    flexWrap: 'wrap',
  },
  layerValue: {
    fontSize: '30px',
    lineHeight: '38px',
    fontWeight: 700,
    fontVariantNumeric: 'tabular-nums',
    color: tokens.colorNeutralForeground1,
  },
  layerCaption: {
    color: tokens.colorNeutralForeground2,
  },
  evidenceBadge: {
    color: tokens.colorNeutralForegroundOnBrand,
    backgroundColor: '#107c10',
    whiteSpace: 'nowrap',
  },
  stats: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(210px, 1fr))',
    gap: '12px',
    marginTop: '18px',
  },
  stat: {
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: '12px 14px',
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  statValue: {
    fontSize: '26px',
    lineHeight: '32px',
    fontWeight: 700,
    fontVariantNumeric: 'tabular-nums',
    color: tokens.colorNeutralForeground1,
  },
  statLabel: {
    color: tokens.colorNeutralForeground2,
    fontWeight: tokens.fontWeightSemibold,
  },
  statHint: {
    color: tokens.colorNeutralForeground3,
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
  basis: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
    marginTop: '16px',
    paddingTop: '12px',
    borderTopWidth: '1px',
    borderTopStyle: 'solid',
    borderTopColor: tokens.colorNeutralStroke2,
  },
  basisText: {
    color: tokens.colorNeutralForeground2,
    maxWidth: '760px',
  },
  actions: {
    display: 'flex',
    gap: '8px',
    flexWrap: 'wrap',
  },
  observed: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    marginTop: '10px',
    color: tokens.colorNeutralForeground2,
  },
  observedBadge: {
    color: tokens.colorNeutralForegroundOnBrand,
    backgroundColor: '#107c10',
    whiteSpace: 'nowrap',
  },
});

/**
 * Shares of the total as whole percentages that add up to 100. Rounded by largest remainder for the
 * same reason the hours are - "33% + 33% + 33%" under a bar that is visibly full reads as a mistake.
 */
function wholeShares(shares: number[]): number[] {
  const total = shares.reduce((sum, s) => sum + s, 0);
  if (total <= 0) return shares.map(() => 0);
  const floors = shares.map((s) => Math.floor(s));
  let leftover = 100 - floors.reduce((sum, s) => sum + s, 0);
  const order = shares
    .map((s, index) => ({ index, remainder: s - Math.floor(s) }))
    .sort((a, b) => b.remainder - a.remainder || a.index - b.index);
  for (let i = 0; leftover > 0 && i < order.length; i++) {
    floors[order[i].index] += 1;
    leftover -= 1;
  }
  return floors;
}

/** One slice of the headline bar: a kind of Copilot work, or Cowork's tasks. */
interface BreakdownSegment {
  key: string;
  label: string;
  colour: string;
  hours: number;
  sharePct: number;
}

/** The label for the Cowork task rate's origin, shown wherever the rate is. */
export function taskRateBasisKey(basis: CoworkTaskProjection['rateBasis']): TranslationKey {
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
 * The Cowork tab's headline: how much time Microsoft 365 Copilot and Cowork could give back.
 *
 * Built to be the number an executive remembers and the number that survives a challenge, which
 * pull in opposite directions - so it does both on the same surface. The figure is large, first and
 * restated in units people feel (full-time capacity, minutes a day each); and it is always a range,
 * always badged as modelled, and always printed with the exact assumptions that produced it, one
 * click from the evidence behind them. Observed Cowork use sits underneath with its own badge, so
 * the one real measurement on this panel is never mistaken for part of the model.
 *
 * Split into its two layers directly under the total, because the evidence behind them differs and
 * a reader justifying Copilot Credits needs to see which part they would be buying: Copilot's layer
 * rests on published Copilot studies and the licences already paid for; Cowork's sits on top, per
 * task, and no study has measured it. Each carries a badge that says which.
 *
 * Leads with full adoption - every Copilot seat holder - because that is the size of the prize; the
 * people ready now are beside it because that is where a rollout starts.
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
  const { assumptions, isCustomised } = timeSaved;

  const full = projectTimeSaved(summary.coworkFullRolloutEstimate, assumptions, options);
  const ready = projectTimeSaved(summary.coworkValueEstimate, assumptions, options);
  const headline = full ?? ready;
  if (!headline) return null;

  const hoursLow = formatCount(headline.hoursLow);
  const hoursHigh = formatCount(headline.hoursHigh);
  const percent = formatNumber(assumptions.conservativeRatio * 100, { maximumFractionDigits: 1 });
  const readyUsers = summary.coworkRecommendedForPolicy;
  const cowork = headline.cowork;

  const segments: BreakdownSegment[] = [
    ...headline.activities.map((a) => ({
      key: a.activity,
      label: t(TIME_SAVED_ACTIVITY_LABEL[a.activity]),
      colour: TIME_SAVED_ACTIVITY_COLOUR[a.activity],
      hours: a.displayHours,
      sharePct: a.sharePct,
    })),
    {
      key: 'cowork',
      label: t('copilotAdoptionCowork.timeSaved.activity.coworkTasks'),
      colour: TIME_SAVED_COWORK_COLOUR,
      hours: cowork.hoursHigh,
      sharePct: cowork.sharePct,
    },
  ];
  const shares = wholeShares(segments.map((s) => s.sharePct));

  const figures = {
    meeting: formatNumber(assumptions.meetingMinutes, { maximumFractionDigits: 2 }),
    email: formatNumber(assumptions.emailMinutes, { maximumFractionDigits: 2 }),
    document: formatNumber(assumptions.documentMinutes, { maximumFractionDigits: 2 }),
    taskMinutes: formatNumber(assumptions.taskMinutes, { maximumFractionDigits: 2 }),
    rate: formatNumber(cowork.tasksPerPerson, { maximumFractionDigits: 2 }),
    percent,
  };

  const basis = t(
    isCustomised ? 'copilotAdoptionCowork.timeSaved.hero.basisCustom' : 'copilotAdoptionCowork.timeSaved.hero.basisDefaults',
    figures,
  );

  return (
    <section className={styles.hero} aria-labelledby="cowork-time-saved-headline" data-print="keep-with-next">
      <div className={styles.top}>
        <Text size={200} weight="semibold" className={styles.eyebrow}>
          {t('copilotAdoptionCowork.timeSaved.hero.eyebrow')}
        </Text>
        <div style={{ display: 'flex', alignItems: 'center', gap: '4px' }}>
          <Tooltip relationship="description" content={t('copilotAdoptionCowork.timeSaved.hero.modelledTooltip')}>
            <Badge appearance="outline" color="informative" size="medium">
              {t('copilotAdoptionCowork.timeSaved.hero.modelledBadge')}
            </Badge>
          </Tooltip>
          <InfoTip
            title={t('copilotAdoptionCowork.timeSaved.hero.infoTitle')}
            content={{
              what: t('copilotAdoptionCowork.timeSaved.hero.info.what'),
              how: t('copilotAdoptionCowork.timeSaved.hero.info.how'),
              formula: t('copilotAdoptionCowork.timeSaved.hero.info.formula', {
                ...figures,
                days: formatNumber(headline.workingDaysPerMonth, { maximumFractionDigits: 2 }),
                hoursPerDay: formatNumber(assumptions.hoursPerDay, { maximumFractionDigits: 2 }),
              }),
              source: t('copilotAdoptionCowork.timeSaved.hero.info.source'),
            }}
          />
        </div>
      </div>

      <span id="cowork-time-saved-headline" className={styles.headline}>
        {t('copilotAdoptionCowork.timeSaved.hero.hoursRange', { range: modelledRange(t, hoursLow, hoursHigh) })}
      </span>
      <Text size={400} className={styles.subline}>
        {full
          ? t(
              plural(
                full.cohortUsers,
                'copilotAdoptionCowork.timeSaved.hero.fullAdoption.one',
                'copilotAdoptionCowork.timeSaved.hero.fullAdoption.other',
              ),
              { users: formatCount(full.cohortUsers) },
            )
          : t(
              plural(
                headline.cohortUsers,
                'copilotAdoptionCowork.timeSaved.hero.readyAdoption.one',
                'copilotAdoptionCowork.timeSaved.hero.readyAdoption.other',
              ),
              { users: formatCount(headline.cohortUsers) },
            )}
      </Text>

      <div className={styles.layers} role="group" aria-label={t('copilotAdoptionCowork.timeSaved.hero.layersAria')}>
        <div className={styles.layer} style={{ borderTopColor: TIME_SAVED_ACTIVITY_COLOUR.meetings }}>
          <div className={styles.layerHead}>
            <Text size={300} weight="semibold">
              {t('copilotAdoptionCowork.timeSaved.hero.copilotLayer.title')}
            </Text>
            <Tooltip relationship="description" content={t('copilotAdoptionCowork.timeSaved.hero.copilotLayer.badgeTooltip')}>
              <Badge size="small" className={styles.evidenceBadge}>
                {t('copilotAdoptionCowork.timeSaved.hero.copilotLayer.badge')}
              </Badge>
            </Tooltip>
          </div>
          <span className={styles.layerValue}>
            {t('copilotAdoptionCowork.timeSaved.hero.layerHours', {
              range: modelledRange(t, formatCount(headline.copilotHoursLow), formatCount(headline.copilotHoursHigh)),
            })}
          </span>
          <Text size={200} className={styles.layerCaption}>
            {t('copilotAdoptionCowork.timeSaved.hero.copilotLayer.caption')}
          </Text>
        </div>

        <div className={styles.layer} style={{ borderTopColor: TIME_SAVED_COWORK_COLOUR }}>
          <div className={styles.layerHead}>
            <Text size={300} weight="semibold">
              {t('copilotAdoptionCowork.timeSaved.hero.coworkLayer.title')}
            </Text>
            <Tooltip relationship="description" content={t('copilotAdoptionCowork.timeSaved.hero.coworkLayer.badgeTooltip')}>
              <Badge size="small" appearance="outline" color="warning">
                {t('copilotAdoptionCowork.timeSaved.hero.coworkLayer.badge')}
              </Badge>
            </Tooltip>
          </div>
          <span className={styles.layerValue}>
            {t('copilotAdoptionCowork.timeSaved.hero.layerHours', {
              range: modelledRange(t, formatCount(cowork.hoursLow), formatCount(cowork.hoursHigh)),
            })}
          </span>
          <Text size={200} className={styles.layerCaption}>
            {t(
              plural(
                cowork.tasks,
                'copilotAdoptionCowork.timeSaved.hero.coworkLayer.caption.one',
                'copilotAdoptionCowork.timeSaved.hero.coworkLayer.caption.other',
              ),
              {
                tasks: formatCount(cowork.tasks),
                minutes: figures.taskMinutes,
              },
            )}
          </Text>
        </div>
      </div>

      <div className={styles.stats}>
        <div className={styles.stat}>
          <span className={styles.statValue}>
            {t('copilotAdoptionCowork.timeSaved.hero.fte.value', {
              range: modelledRange(t, formatModelled(headline.fteLow), formatModelled(headline.fteHigh)),
            })}
          </span>
          <Text size={200} className={styles.statLabel}>
            {t('copilotAdoptionCowork.timeSaved.hero.fte.label')}
          </Text>
          <Text size={100} className={styles.statHint}>
            {t('copilotAdoptionCowork.timeSaved.hero.fte.hint', {
              hours: formatCount(headline.hoursPerFullTimeMonth),
            })}
          </Text>
        </div>

        <div className={styles.stat}>
          <span className={styles.statValue}>
            {t('copilotAdoptionCowork.timeSaved.hero.perPerson.value', {
              range: modelledRange(
                t,
                formatModelled(headline.minutesPerPersonDayLow),
                formatModelled(headline.minutesPerPersonDayHigh),
              ),
            })}
          </span>
          <Text size={200} className={styles.statLabel}>
            {t('copilotAdoptionCowork.timeSaved.hero.perPerson.label')}
          </Text>
          <Text size={100} className={styles.statHint}>
            {t('copilotAdoptionCowork.timeSaved.hero.perPerson.hint', {
              range: modelledRange(
                t,
                formatModelled(headline.copilotMinutesPerPersonDayLow),
                formatModelled(headline.copilotMinutesPerPersonDayHigh),
              ),
            })}
          </Text>
        </div>

        {full && ready && (
          <div className={styles.stat}>
            <span className={styles.statValue}>
              {t('copilotAdoptionCowork.timeSaved.hero.ready.value', {
                range: modelledRange(t, formatCount(ready.hoursLow), formatCount(ready.hoursHigh)),
              })}
            </span>
            <Text size={200} className={styles.statLabel}>
              {t(
                plural(
                  ready.cohortUsers,
                  'copilotAdoptionCowork.timeSaved.hero.ready.label.one',
                  'copilotAdoptionCowork.timeSaved.hero.ready.label.other',
                ),
                { users: formatCount(ready.cohortUsers) },
              )}
            </Text>
            <Text size={100} className={styles.statHint}>
              {t('copilotAdoptionCowork.timeSaved.hero.ready.hint')}
            </Text>
          </div>
        )}
      </div>

      {headline.hoursHigh > 0 && (
        <div className={styles.breakdown}>
          <Text size={200} weight="semibold">
            {t('copilotAdoptionCowork.timeSaved.hero.breakdownTitle')}
          </Text>
          <div className={styles.bar} role="img" aria-label={t('copilotAdoptionCowork.timeSaved.hero.breakdownAria', {
            meetings: `${shares[0]}%`,
            email: `${shares[1]}%`,
            documents: `${shares[2]}%`,
            cowork: `${shares[3]}%`,
          })}>
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
      )}

      <div className={styles.basis}>
        <Text size={200} className={styles.basisText}>
          {basis}{' '}
          {t(taskRateBasisKey(cowork.rateBasis))}
        </Text>
        <div className={styles.actions} data-print="hide">
          <Button appearance="primary" size="small" icon={<Options16Regular />} onClick={onAdjust}>
            {t('copilotAdoptionCowork.timeSaved.hero.adjust')}
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
        </div>
      </div>

      {summary.coworkDetected && summary.coworkUsers > 0 && (
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
      )}
    </section>
  );
}

import { Text, makeStyles, tokens } from '@fluentui/react-components';
import InfoTip from '../shared/InfoTip';
import { formatCount, formatPct } from '../shared/KpiGrid';
import { plural, useT, type TFunction } from '../../i18n';
import type { CopilotAdoptionSummary } from '../../types/copilotAdoption';

const useStyles = makeStyles({
  line: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '8px',
  },
  text: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    flex: 1,
    minWidth: 0,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
});

/** True when the server sent the manager-modelling figures (#641). A server that predates them does not. */
export function hasManagerModelling(summary: CopilotAdoptionSummary): boolean {
  return typeof summary.reportsWithManager === 'number';
}

/**
 * The statement itself, sentence by sentence, in the reader's language.
 *
 * Each sentence is a whole catalog entry rather than a figure spliced between fragments, so a
 * translator can reorder it. A figure the server withheld (null) is never shown as 0%: the sentence
 * that would carry it is replaced by one saying why it is not there.
 */
export function managerModellingSentences(summary: CopilotAdoptionSummary, t: TFunction, detail: boolean): string[] {
  const minSeats = summary.options.minSeatsPerSegment;
  const reports = summary.reportsWithManager ?? 0;
  const known = summary.managersStatusKnown ?? 0;
  const unknown = summary.managersStatusUnknown ?? 0;

  if (reports === 0) return [t('copilotAdoption.page.managerModelling.noManagers')];

  const sentences: string[] = [];
  if (summary.managersActivePct === null || summary.managersActivePct === undefined) {
    sentences.push(t('copilotAdoption.page.managerModelling.tooFewManagers', {
      min: formatCount(minSeats),
      known: formatCount(known),
    }));
  } else {
    sentences.push(t('copilotAdoption.page.managerModelling.managers', {
      pct: formatPct(summary.managersActivePct),
      known: formatCount(known),
    }));

    const activeWith = summary.reportsActiveRatePctManagerActive;
    const activeWithout = summary.reportsActiveRatePctManagerInactive;
    const habitWith = summary.reportsHabitRatePctManagerActive;
    const habitWithout = summary.reportsHabitRatePctManagerInactive;
    if (activeWith != null && activeWithout != null && habitWith != null && habitWithout != null) {
      sentences.push(t('copilotAdoption.page.managerModelling.teams', {
        activeWith: formatPct(activeWith),
        habitWith: formatPct(habitWith),
        activeWithout: formatPct(activeWithout),
        habitWithout: formatPct(habitWithout),
      }));
    } else {
      sentences.push(t('copilotAdoption.page.managerModelling.teamsTooFew', { min: formatCount(minSeats) }));
    }

    if (detail) {
      // Like with like: a manager with a seat is more likely both to use Copilot and to lead a team
      // that was given seats, so the comparison is repeated within each side. Each pair is shown only
      // when both of its rates are, rather than half a comparison.
      const licensedWith = summary.reportsActiveRatePctManagerActiveLicensed;
      const licensedWithout = summary.reportsActiveRatePctManagerInactiveLicensed;
      if (licensedWith != null && licensedWithout != null) {
        sentences.push(t('copilotAdoption.page.managerModelling.seatLicensed', {
          activeWith: formatPct(licensedWith),
          activeWithout: formatPct(licensedWithout),
        }));
      }

      const unlicensedWith = summary.reportsActiveRatePctManagerActiveUnlicensed;
      const unlicensedWithout = summary.reportsActiveRatePctManagerInactiveUnlicensed;
      if (unlicensedWith != null && unlicensedWithout != null) {
        sentences.push(t('copilotAdoption.page.managerModelling.seatUnlicensed', {
          activeWith: formatPct(unlicensedWith),
          activeWithout: formatPct(unlicensedWithout),
        }));
      }
    }
  }

  if (unknown > 0) {
    sentences.push(t(
      plural(unknown, 'copilotAdoption.page.managerModelling.unknown.one', 'copilotAdoption.page.managerModelling.unknown.other'),
      { count: formatCount(unknown) },
    ));
  }

  return sentences;
}

/**
 * Do people managers use Copilot themselves, and how do their direct reports compare (#641)?
 *
 * One statement with its caveat, on the executive view and - with the split by whether the manager
 * holds a seat - above the department table in the analyst view, so the executive figure has
 * somewhere to drill through to. Aggregates only, and open to every reader: no manager is named, and
 * the roll-up by manager carries none of this.
 */
export function ManagerModellingLine({
  summary,
  detail = false,
}: {
  summary: CopilotAdoptionSummary;
  /** Adds the split by whether the manager holds a Copilot seat. */
  detail?: boolean;
}) {
  const styles = useStyles();
  const t = useT();

  if (!hasManagerModelling(summary)) return null;

  const o = summary.options;
  const sentences = managerModellingSentences(summary, t, detail);

  return (
    <div className={styles.line} data-testid="manager-modelling-line">
      <div className={styles.text}>
        <Text weight="semibold">{t('copilotAdoption.page.managerModelling.title')}</Text>
        <Text>{sentences.join(' ')}</Text>
        <Text size={200} className={styles.muted}>{t('copilotAdoption.page.managerModelling.caveat')}</Text>
      </div>
      <InfoTip
        title={t('copilotAdoption.page.managerModelling.title')}
        content={{
          what: t('copilotAdoption.page.managerModelling.tipWhat'),
          how: t('copilotAdoption.page.managerModelling.tipHow', {
            established: o.establishedScore,
            min: formatCount(o.minSeatsPerSegment),
          }),
          formula: t('copilotAdoption.page.managerModelling.tipFormula'),
          source: t('copilotAdoption.page.managerModelling.tipSource'),
        }}
      />
    </div>
  );
}

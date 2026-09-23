import { makeStyles, mergeClasses, tokens, Text, Card, Badge } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import InfoTip from './InfoTip';
import type { InfoTipContent } from './InfoTip';
import { formatDateParts, formatNumber } from '../../i18n';

/**
 * Visual weight of a headline figure. This is judgement, not decoration: on a page that is used to
 * justify licence spend, colouring "42 unused licences" the same as "1,204 interactions" buries the
 * number the reader is supposed to act on.
 */
export type KpiTone = 'neutral' | 'good' | 'warning' | 'critical' | 'opportunity';

const useStyles = makeStyles({
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(190px, 1fr))',
    gap: '12px',
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '14px 16px',
    borderLeftWidth: '4px',
    borderLeftStyle: 'solid',
  },
  // A modelled figure among measured ones. Dashed rather than solid, with a badge, so nobody reading
  // a row of tiles can take the one estimate for another count.
  modelled: {
    borderLeftStyle: 'dashed',
    backgroundColor: tokens.colorBrandBackground2,
  },
  head: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '4px',
  },
  labelGroup: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    flexWrap: 'wrap',
  },
  label: {
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
  },
  value: {
    fontSize: '30px',
    lineHeight: '36px',
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
  },
  hint: {
    color: tokens.colorNeutralForeground3,
  },
});

const TONE_COLOUR: Record<KpiTone, string> = {
  neutral: tokens.colorNeutralStroke1,
  good: tokens.colorPaletteGreenBorderActive,
  warning: tokens.colorPaletteYellowBorderActive,
  critical: tokens.colorPaletteRedBorderActive,
  opportunity: tokens.colorBrandStroke1,
};

export type KpiDefinition = {
  key: string;
  label: string;
  value: ReactNode;
  hint?: string;
  tone?: KpiTone;
  /**
   * What the figure means and exactly how it was calculated. Required rather than optional: a
   * headline number on this page without a stated definition is an assertion nobody can check, and
   * this page exists to be checked.
   */
  info: InfoTipContent;
  /**
   * Set on a figure that is modelled rather than counted, with the (translated) badge text to show.
   * The tile is drawn differently as well as badged: a model sitting unmarked in a row of
   * measurements gets quoted as one.
   */
  modelledBadge?: string;
};

/** A responsive row of headline figures, each carrying its own definition. */
export function KpiGrid({ items }: { items: KpiDefinition[] }) {
  const styles = useStyles();

  return (
    <div className={styles.grid}>
      {items.map((item) => (
        <Card
          key={item.key}
          className={mergeClasses(styles.card, item.modelledBadge ? styles.modelled : undefined)}
          style={{ borderLeftColor: TONE_COLOUR[item.tone ?? 'neutral'] }}
        >
          <div className={styles.head}>
            <span className={styles.labelGroup}>
              <Text size={200} className={styles.label}>
                {item.label}
              </Text>
              {item.modelledBadge && (
                <Badge size="small" appearance="outline" color="informative">
                  {item.modelledBadge}
                </Badge>
              )}
            </span>
            <InfoTip title={item.label} content={item.info} />
          </div>
          <span className={styles.value}>{item.value}</span>
          {item.hint && (
            <Text size={200} className={styles.hint}>
              {item.hint}
            </Text>
          )}
        </Card>
      ))}
    </div>
  );
}

/**
 * Formats a whole number for display, e.g. 12345 -> "12,345" in English, "12.345" in Spanish.
 *
 * Goes through `formatNumber` rather than `toLocaleString()`, which would use the browser's own
 * locale and ignore the language the reader chose. That is not cosmetic: "1,234" is one thousand
 * two hundred and thirty-four to an English reader and one point two three four to a Spanish one,
 * and these figures end up in licence negotiations.
 */
export function formatCount(value: number): string {
  return formatNumber(Math.round(value));
}

/**
 * Formats a percentage to one decimal place, dropping a trailing ".0".
 *
 * The digits go through `formatNumber`, so a Spanish reader gets "12,3%" rather than "12.3%" -
 * `toFixed` always produces a full stop, which in Spanish is the thousands separator.
 */
export function formatPct(value: number): string {
  const rounded = Math.round(value * 10) / 10;
  const digits = rounded % 1 === 0 ? 0 : 1;
  return `${formatNumber(rounded, { minimumFractionDigits: digits, maximumFractionDigits: digits })}%`;
}

/** A UTC ISO date as a short date in the reader's language, or a dash when absent. */
export function formatDate(iso: string | null | undefined): string {
  if (!iso) return '\u2014';
  return formatDateParts(new Date(iso), {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    timeZone: 'UTC',
  });
}

/**
 * A component weight as its share of the engagement score, in percent.
 *
 * The backend divides the weighted sum by the total of the three weights, so quoting a raw weight as
 * a percentage is only correct while they happen to add up to 1. They are configurable, so the UI
 * has to normalise them the same way the scoring does - otherwise a tuned deployment would be shown
 * three components adding up to something other than 100%.
 */
export function weightSharePct(weight: number, weights: number[]): number {
  const sum = weights.reduce((total, w) => total + w, 0);
  return sum <= 0 ? 0 : (weight / sum) * 100;
}

import { makeStyles, tokens, Text, Card } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import InfoTip from './InfoTip';
import type { InfoTipContent } from './InfoTip';

/**
 * Visual weight of a headline figure. This is judgement, not decoration: on a page that is used to
 * justify licence spend, colouring "42 unused seats" the same as "1,204 interactions" buries the
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
  head: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '4px',
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
};

/** A responsive row of headline figures, each carrying its own definition. */
export function KpiGrid({ items }: { items: KpiDefinition[] }) {
  const styles = useStyles();

  return (
    <div className={styles.grid}>
      {items.map((item) => (
        <Card
          key={item.key}
          className={styles.card}
          style={{ borderLeftColor: TONE_COLOUR[item.tone ?? 'neutral'] }}
        >
          <div className={styles.head}>
            <Text size={200} className={styles.label}>
              {item.label}
            </Text>
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

/** Formats a whole number for display, e.g. 12345 -> "12,345". */
export function formatCount(value: number): string {
  return Math.round(value).toLocaleString();
}

/** Formats a percentage to one decimal place, dropping a trailing ".0". */
export function formatPct(value: number): string {
  const rounded = Math.round(value * 10) / 10;
  return `${rounded % 1 === 0 ? rounded.toFixed(0) : rounded.toFixed(1)}%`;
}

/** A UTC ISO date as a short local-format date, or a dash when absent. */
export function formatDate(iso: string | null | undefined): string {
  if (!iso) return '\u2014';
  return new Date(iso).toLocaleDateString(undefined, {
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

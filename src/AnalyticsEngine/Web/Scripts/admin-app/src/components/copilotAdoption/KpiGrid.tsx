import { makeStyles, tokens, Text, Card } from '@fluentui/react-components';
import type { ReactNode } from 'react';

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
};

/** A responsive row of headline figures. */
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
          <Text size={200} className={styles.label}>
            {item.label}
          </Text>
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

import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { ReportCategory } from '../../types/reports';
import { formatValue, seriesColor } from './chartCommon';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'baseline',
    justifyContent: 'center',
    gap: '4px 14px',
    width: '100%',
    padding: '18px 8px',
    lineHeight: '1.5',
  },
  word: {
    cursor: 'default',
    whiteSpace: 'nowrap',
    transition: 'opacity 120ms ease',
    ':hover': {
      opacity: 0.65,
    },
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
});

/** Smallest and largest rendered font size, in pixels. */
const MIN_FONT = 12;
const MAX_FONT = 40;

type WordCloudProps = {
  categories: ReportCategory[];
  /** Unit shown in the tooltip, e.g. "Mentions". */
  valueLabel: string;
};

/**
 * A dependency-free word cloud for "top N phrases" data.
 *
 * Deliberately a simple flow layout rather than a packed/spiral cloud: the point is to convey
 * relative frequency at a glance, and a flow layout stays readable, reflows on narrow screens and
 * needs no layout library. Font size carries the value; colour is from the shared palette purely to
 * make the cloud legible, and does NOT encode anything.
 *
 * Scaling is by square root of the value, not linearly. Phrase frequencies are heavily long-tailed -
 * one dominant phrase would otherwise flatten everything else to the minimum size and the cloud would
 * convey nothing.
 */
export default function WordCloud({ categories, valueLabel }: WordCloudProps) {
  const styles = useStyles();

  if (categories.length === 0) {
    return <div className={styles.empty}>No data for this period.</div>;
  }

  const values = categories.map((c) => c.value);
  const max = Math.max(...values);
  const min = Math.min(...values);

  // sqrt scaling, guarding the degenerate case where every phrase has the same count (max === min),
  // which would otherwise divide by zero and render nothing.
  const spread = Math.sqrt(max) - Math.sqrt(min);
  const fontFor = (v: number) => {
    if (spread <= 0) return (MIN_FONT + MAX_FONT) / 2;
    const t = (Math.sqrt(v) - Math.sqrt(min)) / spread;
    return MIN_FONT + t * (MAX_FONT - MIN_FONT);
  };

  // Largest first reads better than the SQL order and keeps the visual weight at the top.
  const ordered = [...categories].sort((a, b) => b.value - a.value);

  return (
    <div className={styles.root}>
      {ordered.map((c, i) => (
        <Text
          key={c.label}
          className={styles.word}
          title={`${c.label}: ${formatValue(c.value)} ${valueLabel}`}
          style={{
            fontSize: `${fontFor(c.value).toFixed(1)}px`,
            color: seriesColor(i),
            fontWeight: c.value >= max * 0.6 ? 600 : 400,
          }}
        >
          {c.label}
        </Text>
      ))}
    </div>
  );
}

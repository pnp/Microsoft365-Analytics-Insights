import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { ReportCategory } from '../../types/reports';
import { formatValue, seriesColor, seriesColorLight } from './chartCommon';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    width: '100%',
    paddingTop: '4px',
  },
  row: {
    display: 'grid',
    gridTemplateColumns: 'minmax(90px, 160px) 1fr auto',
    alignItems: 'center',
    gap: '12px',
  },
  label: {
    color: tokens.colorNeutralForeground2,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  track: {
    position: 'relative',
    height: '20px',
    borderRadius: '10px',
    backgroundColor: tokens.colorNeutralBackground3,
    overflow: 'hidden',
  },
  bar: {
    height: '100%',
    borderRadius: '10px',
    minWidth: '3px',
    transition: 'width 250ms ease',
  },
  share: {
    position: 'absolute',
    insetBlockStart: 0,
    height: '20px',
    display: 'flex',
    alignItems: 'center',
    fontSize: '11px',
    color: tokens.colorNeutralForeground3,
    paddingInlineStart: '8px',
    pointerEvents: 'none',
  },
  value: {
    fontVariantNumeric: 'tabular-nums',
    textAlign: 'right',
    minWidth: '48px',
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
});

type CategoryBarChartProps = {
  categories: ReportCategory[];
  /** Unit shown in the row title tooltip, e.g. "Interactions". */
  valueLabel: string;
  /**
   * Show each bar's share of the total next to it. Worth it when the categories are parts of one
   * whole (usage by app); misleading when they are not (adoption rate by department, where the
   * values do not sum to anything meaningful), so it is off by default.
   */
  showShare?: boolean;
};

/**
 * A dependency-free horizontal bar chart for "top N" categorical data.
 *
 * Bars are sized relative to the largest value, rounded, and filled with a gradient on the series
 * colour - flat rectangles read as functional, and these charts end up in board packs. The gradient
 * stays within one hue so it never changes which category a colour identifies.
 */
export default function CategoryBarChart({ categories, valueLabel, showShare }: CategoryBarChartProps) {
  const styles = useStyles();

  if (categories.length === 0) {
    return <div className={styles.empty}>No data for this period.</div>;
  }

  const max = Math.max(...categories.map((c) => c.value), 1);
  const total = categories.reduce((sum, c) => sum + c.value, 0);

  return (
    <div className={styles.root}>
      {categories.map((c, i) => {
        const pct = Math.max(1, (c.value / max) * 100);
        const share = total > 0 ? (c.value / total) * 100 : 0;

        return (
          <div className={styles.row} key={c.label} title={`${c.label}: ${formatValue(c.value)} ${valueLabel}`}>
            <Text size={200} className={styles.label}>
              {c.label}
            </Text>
            <div className={styles.track}>
              <div
                className={styles.bar}
                style={{
                  width: `${pct}%`,
                  backgroundImage: `linear-gradient(90deg, ${seriesColor(i)} 0%, ${seriesColorLight(i)} 100%)`,
                }}
              />
              {showShare && share >= 0.5 && (
                <span className={styles.share} style={{ insetInlineStart: `${Math.min(pct, 92)}%` }}>
                  {Math.round(share * 10) / 10}%
                </span>
              )}
            </div>
            <Text size={200} weight="semibold" className={styles.value}>
              {formatValue(c.value)}
            </Text>
          </div>
        );
      })}
    </div>
  );
}

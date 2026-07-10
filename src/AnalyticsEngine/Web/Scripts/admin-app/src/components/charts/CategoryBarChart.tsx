import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { ReportCategory } from '../../types/reports';
import { formatValue, seriesColor } from './chartCommon';

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
    height: '18px',
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralBackground3,
    overflow: 'hidden',
  },
  bar: {
    height: '100%',
    borderRadius: tokens.borderRadiusSmall,
    minWidth: '2px',
    transition: 'width 200ms ease',
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
};

/**
 * A dependency-free horizontal bar chart for "top N" categorical data (e.g. Copilot usage by app,
 * SharePoint activity by operation). Bars are sized relative to the largest value and coloured from
 * the shared palette so they line up with the time-series charts.
 */
export default function CategoryBarChart({ categories, valueLabel }: CategoryBarChartProps) {
  const styles = useStyles();

  if (categories.length === 0) {
    return <div className={styles.empty}>No data for this period.</div>;
  }

  const max = Math.max(...categories.map((c) => c.value), 1);

  return (
    <div className={styles.root}>
      {categories.map((c, i) => {
        const pct = Math.max(1, (c.value / max) * 100);
        return (
          <div className={styles.row} key={c.label} title={`${c.label}: ${formatValue(c.value)} ${valueLabel}`}>
            <Text size={200} className={styles.label}>
              {c.label}
            </Text>
            <div className={styles.track}>
              <div className={styles.bar} style={{ width: `${pct}%`, backgroundColor: seriesColor(i) }} />
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

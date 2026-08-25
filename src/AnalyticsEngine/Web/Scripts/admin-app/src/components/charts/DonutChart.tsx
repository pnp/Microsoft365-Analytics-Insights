import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { ReportCategory } from '../../types/reports';
import { formatValue } from './chartCommon';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    alignItems: 'center',
    gap: '20px',
    flexWrap: 'wrap',
    paddingTop: '4px',
  },
  centre: {
    fontVariantNumeric: 'tabular-nums',
  },
  legend: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    flexGrow: 1,
    minWidth: '160px',
  },
  legendRow: {
    display: 'grid',
    gridTemplateColumns: 'auto 1fr auto',
    alignItems: 'center',
    gap: '8px',
  },
  swatch: {
    width: '10px',
    height: '10px',
    borderRadius: '2px',
  },
  legendLabel: {
    color: tokens.colorNeutralForeground2,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  legendValue: {
    fontVariantNumeric: 'tabular-nums',
    color: tokens.colorNeutralForeground1,
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
});

type DonutChartProps = {
  categories: ReportCategory[];
  /** Colour per slice, in the same order as `categories`. */
  colours: string[];
  /** Shown in the middle of the ring - the total the slices are a share of. */
  centreValue: string;
  centreLabel: string;
  size?: number;
};

/**
 * A dependency-free donut, for the one case a bar chart handles badly: showing that a set of
 * mutually exclusive buckets adds up to a whole.
 *
 * Used for the engagement mix, where the point being made is "this is the entire licensed
 * population, split" - the share is the message, not the individual counts. Anywhere the comparison
 * between categories matters more than their sum, the bar chart is still the right choice.
 */
export default function DonutChart({
  categories,
  colours,
  centreValue,
  centreLabel,
  size = 168,
}: DonutChartProps) {
  const styles = useStyles();

  const total = categories.reduce((sum, c) => sum + c.value, 0);
  if (categories.length === 0 || total <= 0) {
    return <div className={styles.empty}>No data for this period.</div>;
  }

  const radius = size / 2;
  const stroke = Math.max(16, size * 0.19);
  const ringRadius = radius - stroke / 2;
  const circumference = 2 * Math.PI * ringRadius;

  let offset = 0;

  return (
    <div className={styles.root}>
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} role="img" aria-label={centreLabel}>
        <g transform={`rotate(-90 ${radius} ${radius})`}>
          {categories.map((c, i) => {
            const share = c.value / total;
            const dash = share * circumference;
            const circle = (
              <circle
                key={c.label}
                cx={radius}
                cy={radius}
                r={ringRadius}
                fill="none"
                stroke={colours[i % colours.length]}
                strokeWidth={stroke}
                strokeDasharray={`${dash} ${circumference - dash}`}
                strokeDashoffset={-offset}
              >
                <title>{`${c.label}: ${formatValue(c.value)} (${Math.round(share * 1000) / 10}%)`}</title>
              </circle>
            );
            offset += dash;
            return circle;
          })}
        </g>
        <text
          x={radius}
          y={radius - 2}
          textAnchor="middle"
          fontSize={size * 0.19}
          fontWeight={600}
          fill={tokens.colorNeutralForeground1}
          className={styles.centre}
        >
          {centreValue}
        </text>
        <text
          x={radius}
          y={radius + size * 0.13}
          textAnchor="middle"
          fontSize={size * 0.075}
          fill={tokens.colorNeutralForeground3}
        >
          {centreLabel}
        </text>
      </svg>

      <div className={styles.legend}>
        {categories.map((c, i) => (
          <div className={styles.legendRow} key={c.label}>
            <span className={styles.swatch} style={{ backgroundColor: colours[i % colours.length] }} />
            <Text size={200} className={styles.legendLabel}>
              {c.label}
            </Text>
            <Text size={200} weight="semibold" className={styles.legendValue}>
              {formatValue(c.value)} ({Math.round((c.value / total) * 1000) / 10}%)
            </Text>
          </div>
        ))}
      </div>
    </div>
  );
}

import { useMemo, useRef, useState } from 'react';
import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { ReportSeries } from '../../types/reports';
import {
  formatCompact,
  formatValue,
  formatWeek,
  formatWeekLong,
  niceTicks,
  seriesColor,
} from './chartCommon';

// Logical (viewBox) geometry. The SVG scales to the container width, keeping these coordinates.
const W = 960;
const MARGIN = { top: 16, right: 18, bottom: 44, left: 52 };

const useStyles = makeStyles({
  root: {
    position: 'relative',
    width: '100%',
  },
  svg: {
    width: '100%',
    height: 'auto',
    display: 'block',
    overflow: 'visible',
  },
  axisText: {
    fill: tokens.colorNeutralForeground3,
    fontSize: '13px',
  },
  legend: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '4px 16px',
    marginTop: '8px',
  },
  legendItem: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
  },
  swatch: {
    width: '12px',
    height: '12px',
    borderRadius: '2px',
    flexShrink: 0,
  },
  tooltip: {
    position: 'absolute',
    top: '4px',
    pointerEvents: 'none',
    transform: 'translateX(-50%)',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow8,
    padding: '8px 10px',
    minWidth: '120px',
    zIndex: 2,
  },
  tooltipRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
  },
  tooltipLabel: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
});

type TimeSeriesChartProps = {
  series: ReportSeries[];
  /** Unit shown in the tooltip, e.g. "Interactions". */
  valueLabel: string;
  height?: number;
};

/**
 * A dependency-free multi-series line chart for weekly report data. Renders an SVG that scales to
 * its container, with a "nice" y-axis, thinned week labels, and an interactive hover tooltip that
 * reads out every series' value for the hovered week.
 */
export default function TimeSeriesChart({ series, valueLabel, height = 300 }: TimeSeriesChartProps) {
  const styles = useStyles();
  const rootRef = useRef<HTMLDivElement>(null);
  const [hover, setHover] = useState<{ index: number; xPx: number } | null>(null);

  // The x-axis (weeks) comes from the first series; the server gap-fills every series onto the
  // same weekly spine, so all series share these week starts.
  const weeks = series[0]?.points.map((p) => p.weekStart) ?? [];
  const n = weeks.length;

  const H = height;
  const plotLeft = MARGIN.left;
  const plotRight = W - MARGIN.right;
  const plotTop = MARGIN.top;
  const plotBottom = H - MARGIN.bottom;
  const plotW = plotRight - plotLeft;
  const plotH = plotBottom - plotTop;

  const { max, ticks } = useMemo(() => {
    let dataMax = 0;
    for (const s of series) {
      for (const p of s.points) {
        if (p.value > dataMax) dataMax = p.value;
      }
    }
    return niceTicks(dataMax);
  }, [series]);

  if (n === 0) {
    return <div className={styles.empty}>No data for this period.</div>;
  }

  const x = (i: number): number => (n === 1 ? plotLeft + plotW / 2 : plotLeft + (plotW * i) / (n - 1));
  const y = (v: number): number => plotBottom - (plotH * v) / max;

  // Thin the x labels so they don't collide: aim for ~9 labels, always keeping the first and last.
  const labelStep = Math.max(1, Math.ceil(n / 9));
  const showLabel = (i: number): boolean => i === 0 || i === n - 1 || i % labelStep === 0;

  const onMove = (e: React.MouseEvent): void => {
    const rect = rootRef.current?.getBoundingClientRect();
    if (!rect) return;
    const relX = e.clientX - rect.left;
    const logicalX = (relX / rect.width) * W;
    let index = n === 1 ? 0 : Math.round(((logicalX - plotLeft) / plotW) * (n - 1));
    index = Math.max(0, Math.min(n - 1, index));
    // Snap the tooltip to the data point (nicer than following the raw cursor).
    const xPx = (x(index) / W) * rect.width;
    setHover({ index, xPx });
  };

  const clearHover = (): void => setHover(null);

  const showLegend = series.length > 1;
  const tooltipLeft = hover ? Math.max(70, Math.min((rootRef.current?.clientWidth ?? W) - 70, hover.xPx)) : 0;

  return (
    <div className={styles.root} ref={rootRef}>
      <svg
        className={styles.svg}
        viewBox={`0 0 ${W} ${H}`}
        role="img"
        aria-label={`${valueLabel} per week`}
      >
        {/* Gridlines + y-axis labels */}
        {ticks.map((t) => (
          <g key={`grid-${t}`}>
            <line
              x1={plotLeft}
              x2={plotRight}
              y1={y(t)}
              y2={y(t)}
              stroke={tokens.colorNeutralStroke2}
              strokeWidth={1}
            />
            <text
              className={styles.axisText}
              x={plotLeft - 8}
              y={y(t)}
              textAnchor="end"
              dominantBaseline="middle"
            >
              {formatCompact(t)}
            </text>
          </g>
        ))}

        {/* x-axis baseline */}
        <line
          x1={plotLeft}
          x2={plotRight}
          y1={plotBottom}
          y2={plotBottom}
          stroke={tokens.colorNeutralStroke1}
          strokeWidth={1}
        />

        {/* x-axis (week) labels */}
        {weeks.map((wk, i) =>
          showLabel(i) ? (
            <text
              key={`xl-${wk}`}
              className={styles.axisText}
              x={x(i)}
              y={plotBottom + 18}
              textAnchor="middle"
            >
              {formatWeek(wk)}
            </text>
          ) : null,
        )}

        {/* Hover guide */}
        {hover && (
          <line
            x1={x(hover.index)}
            x2={x(hover.index)}
            y1={plotTop}
            y2={plotBottom}
            stroke={tokens.colorNeutralStroke1}
            strokeWidth={1}
            strokeDasharray="4 3"
          />
        )}

        {/* Series lines + points */}
        {series.map((s, si) => {
          const color = seriesColor(si);
          const path = s.points.map((p, i) => `${i === 0 ? 'M' : 'L'} ${x(i)} ${y(p.value)}`).join(' ');
          return (
            <g key={s.name}>
              <path d={path} fill="none" stroke={color} strokeWidth={2.5} strokeLinejoin="round" strokeLinecap="round" />
              {s.points.map((p, i) =>
                hover?.index === i ? (
                  <circle key={`pt-${s.name}-${i}`} cx={x(i)} cy={y(p.value)} r={5} fill={color} stroke={tokens.colorNeutralBackground1} strokeWidth={2} />
                ) : n <= 16 ? (
                  <circle key={`pt-${s.name}-${i}`} cx={x(i)} cy={y(p.value)} r={2.5} fill={color} />
                ) : null,
              )}
            </g>
          );
        })}

        {/* Transparent overlay to capture hover across the whole plot */}
        <rect
          x={plotLeft}
          y={plotTop}
          width={plotW}
          height={plotH}
          fill="transparent"
          onMouseMove={onMove}
          onMouseLeave={clearHover}
        />
      </svg>

      {hover && (
        <div className={styles.tooltip} style={{ left: tooltipLeft }}>
          <Text size={200} weight="semibold" block style={{ marginBottom: 4 }}>
            {formatWeekLong(weeks[hover.index])}
          </Text>
          {series.map((s, si) => (
            <div key={s.name} className={styles.tooltipRow}>
              <span className={styles.tooltipLabel}>
                <span className={styles.swatch} style={{ backgroundColor: seriesColor(si) }} />
                <Text size={200}>{series.length > 1 ? s.name : valueLabel}</Text>
              </span>
              <Text size={200} weight="semibold">
                {formatValue(s.points[hover.index]?.value ?? 0)}
              </Text>
            </div>
          ))}
        </div>
      )}

      {showLegend && (
        <div className={styles.legend}>
          {series.map((s, si) => (
            <span key={s.name} className={styles.legendItem}>
              <span className={styles.swatch} style={{ backgroundColor: seriesColor(si) }} />
              <Text size={200}>{s.name}</Text>
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

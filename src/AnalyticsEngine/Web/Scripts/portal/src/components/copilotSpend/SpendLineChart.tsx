import { useId, useLayoutEffect, useMemo, useState, type KeyboardEvent, type MouseEvent } from 'react';
import { Text, makeStyles, tokens } from '@fluentui/react-components';
import { formatDateParts, useT } from '../../i18n';
import { formatCompact, lineSeriesStyle, niceTicks } from '../charts/chartCommon';
import { formatCredits, formatShortDate, parseUtcDate } from '../copilotInvestment/investmentShared';

export interface SpendLineSeries {
  /** Already translated, or tenant data shown as stored. */
  label: string;
  /** Index into the shared line palette, so a series keeps its colour from chart to chart. */
  colorIndex?: number;
}

export interface SpendLinePoint {
  /** A calendar day: `yyyy-MM-dd`, or an ISO timestamp read as UTC. */
  date: string;
  /** One value per series. Null breaks the line: unknown is never drawn as zero. */
  values: (number | null)[];
  /** The source has not reported this day yet: hatched with diagonal stripes, and its values are unknown. */
  notReported?: boolean;
  /** Marked on the first series, for example a reading taken at the credit limit. */
  marked?: boolean;
  /** Extra lines for the hover readout, already formatted. */
  details?: string[];
  /** What a screen reader hears for this point. Also the text of the hidden list, so a test can find it. */
  label: string;
}

type SpendLineChartProps = {
  points: SpendLinePoint[];
  series: SpendLineSeries[];
  /** The whole chart in one sentence. */
  ariaLabel: string;
  /** The name of the hidden list of points read out instead of the drawing. */
  listLabel: string;
  height?: number;
  formatValue?: (value: number) => string;
  /** Shade the area under the first series. */
  area?: boolean;
  /** Legend and readout wording for a day the source has not reported. */
  notReportedLabel?: string;
  /** Legend and readout wording for a marked point. */
  markerLabel?: string;
  /** Readout wording for a null value. */
  missingLabel?: string;
};

/** Used until the container has been measured, and in tests, where nothing has a width. */
const FALLBACK_WIDTH = 720;
const MARGIN = { top: 14, right: 14, bottom: 26 };
/** A generous average width of one character of an 11px axis label, so the estimate errs towards fewer labels. */
const LABEL_CHAR_WIDTH = 6.5;
const LABEL_GAP = 8;

const useStyles = makeStyles({
  root: { position: 'relative', width: '100%', minWidth: 0 },
  svg: {
    display: 'block',
    width: '100%',
    overflow: 'visible',
    borderRadius: tokens.borderRadiusMedium,
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
  },
  axisText: { fill: tokens.colorNeutralForeground3, fontSize: '12px', fontVariantNumeric: 'tabular-nums' },
  tooltip: {
    position: 'absolute',
    top: '0px',
    transform: 'translateX(-50%)',
    pointerEvents: 'none',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow8,
    padding: '8px 10px',
    minWidth: '140px',
    maxWidth: '260px',
    zIndex: 2,
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  tooltipRow: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '12px' },
  tooltipLabel: { display: 'inline-flex', alignItems: 'center', gap: '6px' },
  muted: { color: tokens.colorNeutralForeground3 },
  legend: { display: 'flex', flexWrap: 'wrap', gap: '4px 16px', marginTop: '6px' },
  legendItem: { display: 'inline-flex', alignItems: 'center', gap: '6px' },
  swatch: { width: '14px', height: '3px', borderRadius: '2px', flexShrink: 0 },
  hatch: {
    width: '14px',
    height: '12px',
    flexShrink: 0,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundImage: `repeating-linear-gradient(45deg, ${tokens.colorNeutralStroke2} 0 2px, transparent 2px 5px)`,
  },
  marker: { width: '10px', height: '10px', borderRadius: '50%', flexShrink: 0, backgroundColor: tokens.colorPaletteRedBackground3 },
  srOnly: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    marginTop: '-1px',
    marginRight: '-1px',
    marginBottom: '-1px',
    marginLeft: '-1px',
    overflow: 'hidden',
    clip: 'rect(0 0 0 0)',
    whiteSpace: 'nowrap',
  },
});

/** Takes the element itself (from a callback ref), so a chart that first renders empty still measures once it is drawn. */
function useMeasuredWidth(element: HTMLDivElement | null): number {
  const [width, setWidth] = useState(0);
  useLayoutEffect(() => {
    if (!element) return undefined;
    const update = () => setWidth(Math.round(element.clientWidth));
    update();
    if (typeof ResizeObserver === 'undefined') return undefined;
    const observer = new ResizeObserver(update);
    observer.observe(element);
    return () => observer.disconnect();
  }, [element]);
  return width;
}

function shortDay(date: string): string {
  return formatDateParts(parseUtcDate(date), { day: 'numeric', month: 'short', timeZone: 'UTC' });
}

/**
 * A line chart for daily spend figures. Unlike the weekly report chart it draws at the width it is given, so its text stays
 * the same size in a half-width card as in a full-width one. A null value is a gap, a day the source has not reported is
 * hatched with diagonal stripes rather than drawn as zero, and every point is also listed for a screen reader.
 */
export default function SpendLineChart({
  points,
  series,
  ariaLabel,
  listLabel,
  height = 200,
  formatValue = formatCredits,
  area = false,
  notReportedLabel,
  markerLabel,
  missingLabel,
}: SpendLineChartProps) {
  const t = useT();
  const styles = useStyles();
  const [rootElement, setRootElement] = useState<HTMLDivElement | null>(null);
  const measured = useMeasuredWidth(rootElement);
  const [hover, setHover] = useState<number | null>(null);
  const idBase = useId().replace(/[^a-zA-Z0-9_-]/g, '');

  const n = points.length;
  const W = measured > 0 ? measured : FALLBACK_WIDTH;
  const H = height;

  const { max, ticks } = useMemo(() => {
    let dataMax = 0;
    for (const point of points) {
      for (const value of point.values) {
        if (value !== null && Number.isFinite(value) && value > dataMax) dataMax = value;
      }
    }
    return niceTicks(dataMax, H < 160 ? 3 : 4);
  }, [points, H]);

  if (n === 0) return null;

  const tickLabels = ticks.map((tick) => formatCompact(tick));
  const plotLeft = Math.max(28, 10 + Math.max(...tickLabels.map((label) => label.length)) * 7);
  const plotRight = W - MARGIN.right;
  const plotTop = MARGIN.top;
  const plotBottom = H - MARGIN.bottom;
  const plotW = Math.max(10, plotRight - plotLeft);
  const plotH = Math.max(10, plotBottom - plotTop);
  const step = n > 1 ? plotW / (n - 1) : plotW;

  const x = (i: number): number => (n === 1 ? plotLeft + plotW / 2 : plotLeft + step * i);
  const y = (value: number): number => plotBottom - (plotH * value) / max;

  // Thin the day labels to what fits, always keeping the last day. The last label is right-aligned, so whether the one
  // before it collides depends on pixels, not on how many days lie between them: each label's width is estimated.
  const maxLabels = Math.max(2, Math.floor(plotW / 64));
  const labelStep = Math.max(1, Math.ceil(n / maxLabels));
  const dayLabels = points.map((point) => shortDay(point.date));
  const anchorOf = (i: number): 'start' | 'middle' | 'end' => (n === 1 ? 'middle' : i === 0 ? 'start' : i === n - 1 ? 'end' : 'middle');
  const labelSpan = (i: number): [number, number] => {
    const width = dayLabels[i].length * LABEL_CHAR_WIDTH;
    const anchor = anchorOf(i);
    if (anchor === 'start') return [x(i), x(i) + width];
    if (anchor === 'end') return [x(i) - width, x(i)];
    return [x(i) - width / 2, x(i) + width / 2];
  };
  const shownLabels = new Set<number>([n - 1]);
  const lastLabelStart = labelSpan(n - 1)[0];
  let previousLabelEnd = Number.NEGATIVE_INFINITY;
  for (let i = 0; i < n - 1; i += labelStep) {
    const [start, end] = labelSpan(i);
    if (start >= previousLabelEnd + LABEL_GAP && end <= lastLabelStart - LABEL_GAP) {
      shownLabels.add(i);
      previousLabelEnd = end;
    }
  }

  // Runs of days the source has not reported, shaded from half a day before to half a day after.
  const unreported: { from: number; to: number }[] = [];
  points.forEach((point, i) => {
    if (!point.notReported) return;
    const last = unreported[unreported.length - 1];
    if (last && last.to === i - 1) last.to = i;
    else unreported.push({ from: i, to: i });
  });
  const edge = (i: number, side: -1 | 1): number => {
    if (n === 1) return side < 0 ? plotLeft : plotRight;
    return Math.min(plotRight, Math.max(plotLeft, x(i) + (side * step) / 2));
  };

  const segmentsOf = (seriesIndex: number): { i: number; v: number }[][] => {
    const segments: { i: number; v: number }[][] = [];
    let current: { i: number; v: number }[] = [];
    points.forEach((point, i) => {
      const value = point.values[seriesIndex];
      if (value === null || value === undefined || !Number.isFinite(value)) {
        if (current.length) segments.push(current);
        current = [];
      } else {
        current.push({ i, v: value });
      }
    });
    if (current.length) segments.push(current);
    return segments;
  };

  const indexAt = (clientX: number, rect: DOMRect): number => {
    const logicalX = ((clientX - rect.left) / Math.max(1, rect.width)) * W;
    const index = n === 1 ? 0 : Math.round((logicalX - plotLeft) / step);
    return Math.max(0, Math.min(n - 1, index));
  };
  const onMove = (event: MouseEvent<SVGRectElement>) => {
    const svg = event.currentTarget.ownerSVGElement;
    if (svg) setHover(indexAt(event.clientX, svg.getBoundingClientRect()));
  };
  const onKeyDown = (event: KeyboardEvent<SVGSVGElement>) => {
    const current = hover ?? n - 1;
    const next =
      event.key === 'ArrowLeft' ? current - 1
      : event.key === 'ArrowRight' ? current + 1
      : event.key === 'Home' ? 0
      : event.key === 'End' ? n - 1
      : null;
    if (next === null) return;
    event.preventDefault();
    setHover(Math.max(0, Math.min(n - 1, next)));
  };

  const hovered = hover !== null ? points[hover] : null;
  const tooltipLeft = hover !== null ? Math.max(80, Math.min(W - 80, x(hover))) : 0;
  const hasMarkers = points.some((point) => point.marked);
  const showLegend = series.length > 1 || (unreported.length > 0 && !!notReportedLabel) || (hasMarkers && !!markerLabel);
  const colorOf = (seriesIndex: number) => lineSeriesStyle(series[seriesIndex]?.colorIndex ?? seriesIndex).color;
  const gradientId = `${idBase}-area`;
  const hatchId = `${idBase}-hatch`;
  const missing = missingLabel ?? t('charts.timeSeries.noData');

  return (
    <div className={styles.root} ref={setRootElement}>
      <svg
        className={styles.svg}
        width="100%"
        height={H}
        viewBox={`0 0 ${W} ${H}`}
        role="img"
        aria-label={ariaLabel}
        tabIndex={0}
        onKeyDown={onKeyDown}
        onFocus={() => setHover((current) => current ?? n - 1)}
        onBlur={() => setHover(null)}
      >
        <defs>
          <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor={colorOf(0)} stopOpacity={0.26} />
            <stop offset="100%" stopColor={colorOf(0)} stopOpacity={0.02} />
          </linearGradient>
          <pattern id={hatchId} patternUnits="userSpaceOnUse" width="6" height="6" patternTransform="rotate(45)">
            <line x1="0" y1="0" x2="0" y2="6" stroke={tokens.colorNeutralStroke2} strokeWidth={2} />
          </pattern>
        </defs>

        {unreported.map((run) => (
          <rect
            key={`nr-${run.from}`}
            x={edge(run.from, -1)}
            y={plotTop}
            width={Math.max(2, edge(run.to, 1) - edge(run.from, -1))}
            height={plotH}
            fill={`url(#${hatchId})`}
            opacity={0.8}
          />
        ))}

        {ticks.map((tick, i) => (
          <g key={`tick-${tick}`}>
            <line x1={plotLeft} x2={plotRight} y1={y(tick)} y2={y(tick)} stroke={tokens.colorNeutralStroke2} strokeWidth={1} />
            <text className={styles.axisText} x={plotLeft - 6} y={y(tick)} textAnchor="end" dominantBaseline="middle">
              {tickLabels[i]}
            </text>
          </g>
        ))}
        <line x1={plotLeft} x2={plotRight} y1={plotBottom} y2={plotBottom} stroke={tokens.colorNeutralStroke1} strokeWidth={1} />

        {points.map((point, i) =>
          shownLabels.has(i) ? (
            <text key={`x-${point.date}-${i}`} className={styles.axisText} x={x(i)} y={plotBottom + 17} textAnchor={anchorOf(i)}>
              {dayLabels[i]}
            </text>
          ) : null,
        )}

        {hover !== null && (
          <line x1={x(hover)} x2={x(hover)} y1={plotTop} y2={plotBottom} stroke={tokens.colorNeutralStroke1} strokeWidth={1} strokeDasharray="4 3" />
        )}

        {series.map((line, seriesIndex) => {
          const color = colorOf(seriesIndex);
          const segments = segmentsOf(seriesIndex);
          return (
            <g key={`${line.label}-${seriesIndex}`}>
              {area && seriesIndex === 0 &&
                segments
                  .filter((segment) => segment.length > 1)
                  .map((segment) => (
                    <path
                      key={`area-${segment[0].i}`}
                      d={`M ${x(segment[0].i)} ${plotBottom} ${segment.map((p) => `L ${x(p.i)} ${y(p.v)}`).join(' ')} L ${x(segment[segment.length - 1].i)} ${plotBottom} Z`}
                      fill={`url(#${gradientId})`}
                    />
                  ))}
              {segments.map((segment) => (
                <path
                  key={`line-${segment[0].i}`}
                  d={segment.map((p, k) => `${k === 0 ? 'M' : 'L'} ${x(p.i)} ${y(p.v)}`).join(' ')}
                  fill="none"
                  stroke={color}
                  strokeWidth={2.5}
                  strokeLinejoin="round"
                  strokeLinecap="round"
                />
              ))}
              {segments.flatMap((segment) =>
                segment.map((p) =>
                  hover === p.i ? (
                    <circle key={`dot-${p.i}`} cx={x(p.i)} cy={y(p.v)} r={5} fill={color} stroke={tokens.colorNeutralBackground1} strokeWidth={2} />
                  ) : n <= 16 || segment.length === 1 ? (
                    <circle key={`dot-${p.i}`} cx={x(p.i)} cy={y(p.v)} r={3} fill={color} />
                  ) : null,
                ),
              )}
            </g>
          );
        })}

        {points.map((point, i) => {
          if (!point.marked) return null;
          const value = point.values[0];
          const cy = value === null || value === undefined ? plotTop + 6 : y(value);
          return (
            <circle key={`mark-${i}`} cx={x(i)} cy={cy} r={6} fill={tokens.colorPaletteRedBackground3} stroke={tokens.colorNeutralBackground1} strokeWidth={2} />
          );
        })}

        <rect
          x={plotLeft - step / 2}
          y={plotTop}
          width={plotW + step}
          height={plotH}
          fill="transparent"
          onMouseMove={onMove}
          onMouseLeave={() => setHover(null)}
        />
      </svg>

      {hovered && (
        <div className={styles.tooltip} style={{ left: tooltipLeft }} aria-hidden="true">
          <Text size={200} weight="semibold">{formatShortDate(hovered.date)}</Text>
          {hovered.notReported && notReportedLabel ? (
            <Text size={200} className={styles.muted}>{notReportedLabel}</Text>
          ) : (
            series.map((line, seriesIndex) => {
              const value = hovered.values[seriesIndex];
              return (
                <div key={`${line.label}-${seriesIndex}`} className={styles.tooltipRow}>
                  <span className={styles.tooltipLabel}>
                    <span className={styles.swatch} style={{ backgroundColor: colorOf(seriesIndex) }} />
                    <Text size={200}>{line.label}</Text>
                  </span>
                  <Text size={200} weight="semibold">
                    {value === null || value === undefined ? missing : formatValue(value)}
                  </Text>
                </div>
              );
            })
          )}
          {hovered.details?.map((detail) => (
            <Text key={detail} size={200} className={styles.muted}>{detail}</Text>
          ))}
          {hovered.marked && markerLabel && <Text size={200} weight="semibold">{markerLabel}</Text>}
        </div>
      )}

      {showLegend && (
        <div className={styles.legend} aria-hidden="true">
          {series.length > 1 &&
            series.map((line, seriesIndex) => (
              <span key={`${line.label}-${seriesIndex}`} className={styles.legendItem}>
                <span className={styles.swatch} style={{ backgroundColor: colorOf(seriesIndex) }} />
                <Text size={200}>{line.label}</Text>
              </span>
            ))}
          {unreported.length > 0 && notReportedLabel && (
            <span className={styles.legendItem}>
              <span className={styles.hatch} />
              <Text size={200}>{notReportedLabel}</Text>
            </span>
          )}
          {hasMarkers && markerLabel && (
            <span className={styles.legendItem}>
              <span className={styles.marker} />
              <Text size={200}>{markerLabel}</Text>
            </span>
          )}
        </div>
      )}

      <ul className={styles.srOnly} aria-label={listLabel}>
        {points.map((point, i) => (
          <li key={`${point.date}-${i}`} aria-label={point.label}>{point.label}</li>
        ))}
      </ul>
    </div>
  );
}

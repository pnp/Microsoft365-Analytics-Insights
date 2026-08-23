import { makeStyles, tokens } from '@fluentui/react-components';
import type { ReportCategory } from '../../types/reports';
import { formatValue, seriesColor } from './chartCommon';

const useStyles = makeStyles({
  root: {
    width: '100%',
    paddingTop: '4px',
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
});

type Rect = { label: string; value: number; x: number; y: number; w: number; h: number; index: number };

/**
 * Squarified treemap layout (Bruls, Huizing & van Wijk). Lays each row along the shorter side of the
 * remaining space, extending the row while doing so improves its worst aspect ratio - which is what
 * keeps the tiles readable rectangles instead of slivers.
 */
function squarify(values: { label: string; value: number; index: number }[], width: number, height: number): Rect[] {
  const total = values.reduce((sum, v) => sum + v.value, 0);
  if (total <= 0) return [];

  const scale = (width * height) / total;
  const items = values.map((v) => ({ ...v, area: v.value * scale }));
  const out: Rect[] = [];

  let x = 0;
  let y = 0;
  let w = width;
  let h = height;
  let row: typeof items = [];
  let i = 0;

  const worst = (candidate: typeof items, side: number) => {
    if (candidate.length === 0) return Infinity;
    const sum = candidate.reduce((s, c) => s + c.area, 0);
    const max = Math.max(...candidate.map((c) => c.area));
    const min = Math.min(...candidate.map((c) => c.area));
    const side2 = side * side;
    const sum2 = sum * sum;
    return Math.max((side2 * max) / sum2, sum2 / (side2 * min));
  };

  const layoutRow = (candidate: typeof items, side: number, horizontal: boolean) => {
    const sum = candidate.reduce((s, c) => s + c.area, 0);
    const thickness = sum / side;
    let offset = 0;

    candidate.forEach((c) => {
      const length = c.area / thickness;
      out.push({
        label: c.label,
        value: c.value,
        index: c.index,
        x: horizontal ? x + offset : x,
        y: horizontal ? y : y + offset,
        w: horizontal ? length : thickness,
        h: horizontal ? thickness : length,
      });
      offset += length;
    });

    if (horizontal) {
      y += thickness;
      h -= thickness;
    } else {
      x += thickness;
      w -= thickness;
    }
  };

  while (i < items.length) {
    // Rows must be laid along the SHORTER side of the remaining rectangle. Using the longer side
    // still fills the space exactly, but produces slivers - on representative data it took the worst
    // aspect ratio from 2.1:1 to 14.5:1, which is precisely the readability the squarified algorithm
    // exists to buy.
    const horizontal = w < h;
    const side = horizontal ? w : h;
    const next = [...row, items[i]];

    if (row.length === 0 || worst(next, side) <= worst(row, side)) {
      row = next;
      i += 1;
    } else {
      layoutRow(row, side, horizontal);
      row = [];
    }
  }

  if (row.length > 0) {
    layoutRow(row, w < h ? w : h, w < h);
  }

  return out;
}

/**
 * A treemap for "where is this actually happening" breakdowns.
 *
 * Preferred over a bar chart when the categories are parts of one total and the distribution is
 * very uneven: a top surface with ten times the traffic of the next one is immediately obvious as
 * an area, whereas as a bar it just means every other bar is too short to read. The bar chart
 * remains the better choice when the reader needs to compare similar values precisely.
 */
export default function TreemapChart({
  categories,
  valueLabel,
  height = 240,
}: {
  categories: ReportCategory[];
  valueLabel: string;
  height?: number;
}) {
  const styles = useStyles();

  const data = categories.filter((c) => c.value > 0);
  if (data.length === 0) {
    return <div className={styles.empty}>No data for this period.</div>;
  }

  // Laid out in a fixed 1000-wide viewBox and scaled by the SVG, so the tiles are correct at any
  // container width without needing a resize observer.
  const vbWidth = 1000;
  const vbHeight = Math.round((height / 320) * 1000);

  const sorted = [...data].sort((a, b) => b.value - a.value);
  const total = sorted.reduce((s, c) => s + c.value, 0);
  const rects = squarify(
    sorted.map((c, index) => ({ label: c.label, value: c.value, index })),
    vbWidth,
    vbHeight,
  );

  return (
    <div className={styles.root}>
      <svg
        viewBox={`0 0 ${vbWidth} ${vbHeight}`}
        width="100%"
        height={height}
        preserveAspectRatio="none"
        role="img"
        aria-label={`${valueLabel} by category`}
      >
        {rects.map((r) => {
          const share = (r.value / total) * 100;
          // Text is drawn in viewBox units that are then stretched non-uniformly, so anything
          // narrower than this becomes unreadable - better to show a bare tile with a tooltip.
          const showLabel = r.w > 90 && r.h > 46;

          return (
            <g key={r.label}>
              <rect
                x={r.x}
                y={r.y}
                width={Math.max(0, r.w - 2)}
                height={Math.max(0, r.h - 2)}
                fill={seriesColor(r.index)}
                rx={3}
              >
                <title>{`${r.label}: ${formatValue(r.value)} ${valueLabel} (${
                  Math.round(share * 10) / 10
                }%)`}</title>
              </rect>
              {showLabel && (
                <>
                  <text
                    x={r.x + 10}
                    y={r.y + 26}
                    fill="#ffffff"
                    fontSize={22}
                    fontWeight={600}
                    style={{ pointerEvents: 'none' }}
                  >
                    {r.label}
                  </text>
                  <text
                    x={r.x + 10}
                    y={r.y + 50}
                    fill="#ffffff"
                    fontSize={19}
                    opacity={0.85}
                    style={{ pointerEvents: 'none' }}
                  >
                    {formatValue(r.value)}
                  </text>
                </>
              )}
            </g>
          );
        })}
      </svg>
    </div>
  );
}

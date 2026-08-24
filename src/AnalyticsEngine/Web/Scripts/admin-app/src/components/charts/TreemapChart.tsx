import { makeStyles, tokens } from '@fluentui/react-components';
import type { ReportCategory } from '../../types/reports';
import { formatValue, seriesColor } from './chartCommon';

const useStyles = makeStyles({
  root: {
    position: 'relative',
    width: '100%',
    marginTop: '4px',
  },
  tile: {
    position: 'absolute',
    borderRadius: tokens.borderRadiusSmall,
    overflow: 'hidden',
    padding: '6px 8px',
    boxSizing: 'border-box',
    color: '#ffffff',
    display: 'flex',
    flexDirection: 'column',
    gap: '1px',
  },
  label: {
    fontSize: '13px',
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: '16px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  value: {
    fontSize: '12px',
    lineHeight: '15px',
    opacity: 0.9,
    fontVariantNumeric: 'tabular-nums',
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
 * Preferred over a bar chart when the categories are parts of one total and the distribution is very
 * uneven: a top surface with ten times the traffic of the next is immediately obvious as an area,
 * whereas as a bar it just means every other bar is too short to read. The bar chart remains the
 * better choice when the reader needs to compare similar values precisely.
 *
 * Rendered as positioned HTML rather than SVG. The layout is computed in a fixed coordinate space
 * and the tiles are placed in percentages, so the browser resolves them against the real container
 * size. An SVG version has to choose between letterboxing and `preserveAspectRatio="none"`, and the
 * latter scales the coordinate system unevenly - which stretches the glyphs horizontally and makes
 * every label look subtly wrong. HTML text is laid out after its box is sized, so it is always drawn
 * at its true aspect ratio, and it gets ellipsis and hit-testing for free.
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

  const sorted = [...data].sort((a, b) => b.value - a.value);
  const total = sorted.reduce((s, c) => s + c.value, 0);

  // Laid out in a fixed coordinate space, then expressed as percentages. The ratio used here decides
  // how the squarifier splits, not whether the result fits, so keeping it fixed makes the tiling
  // stable across resizes rather than reflowing every tile on every pixel of width change.
  const LAYOUT_W = 1000;
  const LAYOUT_H = 600;

  const rects = squarify(
    sorted.map((c, index) => ({ label: c.label, value: c.value, index })),
    LAYOUT_W,
    LAYOUT_H,
  );

  return (
    <div className={styles.root} style={{ height: `${height}px` }} role="img" aria-label={`${valueLabel} by category`}>
      {rects.map((r) => {
        const share = (r.value / total) * 100;
        const heightPx = (r.h / LAYOUT_H) * height;

        return (
          <div
            key={r.label}
            className={styles.tile}
            style={{
              left: `${(r.x / LAYOUT_W) * 100}%`,
              top: `${(r.y / LAYOUT_H) * 100}%`,
              // The tiles tile exactly, so each is inset by a hairline to let the page background
              // through as a separator. Done inline rather than with a border because Griffel
              // rejects the border shorthands, and per-side longhands here would be four rules.
              width: `calc(${(r.w / LAYOUT_W) * 100}% - 3px)`,
              height: `calc(${(r.h / LAYOUT_H) * 100}% - 3px)`,
              backgroundColor: seriesColor(r.index),
            }}
            title={`${r.label}: ${formatValue(r.value)} ${valueLabel} (${Math.round(share * 10) / 10}%)`}
          >
            <span className={styles.label}>{r.label}</span>
            {/* Only show the value when the tile is tall enough for a second line. A clipped
                half-height number is worse than none, and the tooltip always carries it. */}
            {heightPx >= 40 && <span className={styles.value}>{formatValue(r.value)}</span>}
          </div>
        );
      })}
    </div>
  );
}

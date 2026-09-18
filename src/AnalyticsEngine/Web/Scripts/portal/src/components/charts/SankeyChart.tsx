import { useMemo, useState } from 'react';
import { makeStyles, tokens, Text } from '@fluentui/react-components';
import { formatValue, seriesColor } from './chartCommon';

const useStyles = makeStyles({
  root: { width: '100%' },
  svg: { width: '100%', display: 'block', overflow: 'visible' },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
  caption: {
    color: tokens.colorNeutralForeground3,
    display: 'block',
    marginTop: '8px',
  },
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
  tooltip: {
    position: 'absolute',
    pointerEvents: 'none',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow8,
    padding: '8px 10px',
    zIndex: 10,
    maxWidth: '320px',
  },
  tipLine: { display: 'block', color: tokens.colorNeutralForeground2 },
  wrap: { position: 'relative', width: '100%' },
});

export type SankeyFlow = {
  /** Left-hand node label. */
  sourceLabel: string;
  /** Right-hand node label. */
  targetLabel: string;
  value: number;
  /** Drawn in a muted tone and labelled, rather than as a journey. */
  isSelfFlow?: boolean;
  /** Extra lines for the hover tooltip. */
  detail?: string[];
};

type SankeyChartProps = {
  flows: SankeyFlow[];
  /** Unit for the values, e.g. "visits". */
  valueLabel: string;
  height?: number;
  /** Shown under the diagram, e.g. how much of the traffic it covers. */
  caption?: string;
};

const W = 1000;
const NODE_W = 12;
const NODE_GAP = 6;
const LABEL_PAD = 8;
const SIDE = 210;

type Node = { key: string; label: string; value: number; y: number; h: number };

/**
 * A dependency-free Sankey for two-column flows: where visits STARTED on the left, where the same
 * visits ENDED on the right, with ribbon width proportional to the number of visits.
 *
 * Deliberately two columns rather than a general multi-stage Sankey. The question it answers is
 * "did people who landed here get where they were going?", which is a pairing of two points in a
 * visit, not a full path tree - and a full tree of intranet paths is unreadable at any real
 * traffic volume.
 *
 * Self-flows (ended on the page it started on) are kept and drawn muted rather than hidden. They
 * are usually the largest single band on an intranet, and dropping them would make the diagram
 * imply a journey happened where none did.
 */
export default function SankeyChart({ flows, valueLabel, height = 420, caption }: SankeyChartProps) {
  const styles = useStyles();
  const [hover, setHover] = useState<{ i: number; x: number; y: number } | null>(null);

  const model = useMemo(() => {
    const positive = flows.filter((f) => f.value > 0);
    if (positive.length === 0) return null;

    // A page can be both a start and an end, so the two columns are indexed separately - the same
    // label on the left and on the right is the same page but a different node.
    const sources = new Map<string, number>();
    const targets = new Map<string, number>();
    for (const f of positive) {
      sources.set(f.sourceLabel, (sources.get(f.sourceLabel) ?? 0) + f.value);
      targets.set(f.targetLabel, (targets.get(f.targetLabel) ?? 0) + f.value);
    }

    const plotTop = 8;
    const plotH = height - 16;

    const build = (totals: Map<string, number>): Map<string, Node> => {
      const entries = [...totals.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
      const sum = entries.reduce((s, [, v]) => s + v, 0);
      const gaps = NODE_GAP * Math.max(0, entries.length - 1);
      const usable = Math.max(1, plotH - gaps);

      const nodes = new Map<string, Node>();
      let y = plotTop;
      for (const [label, value] of entries) {
        // Floor of 2px so a tiny flow is still visible and still hoverable.
        const h = Math.max(2, sum > 0 ? (value / sum) * usable : 0);
        nodes.set(label, { key: label, label, value, y, h });
        y += h + NODE_GAP;
      }
      return nodes;
    };

    const left = build(sources);
    const right = build(targets);

    // Ribbons stack down each node in the same order the flows are drawn, so the bands leaving a
    // node and the bands arriving at one always add up to that node's height.
    const leftCursor = new Map<string, number>();
    const rightCursor = new Map<string, number>();

    const ordered = [...positive].sort((a, b) => b.value - a.value);
    const ribbons = ordered.map((f, i) => {
      const s = left.get(f.sourceLabel)!;
      const t = right.get(f.targetLabel)!;
      const sSum = sources.get(f.sourceLabel)!;
      const tSum = targets.get(f.targetLabel)!;

      const sh = (f.value / sSum) * s.h;
      const th = (f.value / tSum) * t.h;

      const sy = s.y + (leftCursor.get(f.sourceLabel) ?? 0);
      const ty = t.y + (rightCursor.get(f.targetLabel) ?? 0);

      leftCursor.set(f.sourceLabel, (leftCursor.get(f.sourceLabel) ?? 0) + sh);
      rightCursor.set(f.targetLabel, (rightCursor.get(f.targetLabel) ?? 0) + th);

      return { flow: f, index: i, sy, sh, ty, th };
    });

    return { left: [...left.values()], right: [...right.values()], ribbons };
  }, [flows, height]);

  if (!model) {
    return <div className={styles.empty}>No visit flows for this period.</div>;
  }

  const x0 = SIDE;
  const x1 = W - SIDE - NODE_W;

  return (
    <div className={styles.wrap}>
      <svg
        className={styles.svg}
        viewBox={`0 0 ${W} ${height}`}
        preserveAspectRatio="xMidYMid meet"
        role="img"
        aria-label={`Sankey diagram of where visits started and ended, measured in ${valueLabel}.`}
      >
        {model.ribbons.map((r) => {
          const midX = (x0 + NODE_W + x1) / 2;
          const top = `M ${x0 + NODE_W} ${r.sy} C ${midX} ${r.sy}, ${midX} ${r.ty}, ${x1} ${r.ty}`;
          const bottom =
            `L ${x1} ${r.ty + r.th} C ${midX} ${r.ty + r.th}, ${midX} ${r.sy + r.sh}, ${x0 + NODE_W} ${r.sy + r.sh} Z`;
          const active = hover?.i === r.index;

          return (
            <path
              key={`${r.flow.sourceLabel}\u0000${r.flow.targetLabel}`}
              d={`${top} ${bottom}`}
              fill={r.flow.isSelfFlow ? tokens.colorNeutralForeground4 : seriesColor(r.index)}
              opacity={active ? 0.85 : r.flow.isSelfFlow ? 0.3 : 0.45}
              onMouseEnter={(e) =>
                setHover({ i: r.index, x: e.nativeEvent.offsetX, y: e.nativeEvent.offsetY })
              }
              onMouseMove={(e) =>
                setHover({ i: r.index, x: e.nativeEvent.offsetX, y: e.nativeEvent.offsetY })
              }
              onMouseLeave={() => setHover(null)}
            >
              <title>
                {`${r.flow.sourceLabel} \u2192 ${r.flow.targetLabel}: ${formatValue(r.flow.value)} ${valueLabel}`}
              </title>
            </path>
          );
        })}

        {model.left.map((n) => (
          <g key={`l-${n.key}`}>
            <rect x={x0} y={n.y} width={NODE_W} height={n.h} rx={2} fill={tokens.colorBrandBackground} />
            <text
              x={x0 - LABEL_PAD}
              y={n.y + n.h / 2}
              textAnchor="end"
              dominantBaseline="middle"
              fontSize={13}
              fill={tokens.colorNeutralForeground2}
            >
              {truncate(n.label)}
              <title>{`${n.label}: ${formatValue(n.value)} ${valueLabel}`}</title>
            </text>
          </g>
        ))}

        {model.right.map((n) => (
          <g key={`r-${n.key}`}>
            <rect x={x1} y={n.y} width={NODE_W} height={n.h} rx={2} fill={tokens.colorPaletteDarkOrangeBorderActive} />
            <text
              x={x1 + NODE_W + LABEL_PAD}
              y={n.y + n.h / 2}
              textAnchor="start"
              dominantBaseline="middle"
              fontSize={13}
              fill={tokens.colorNeutralForeground2}
            >
              {truncate(n.label)}
              <title>{`${n.label}: ${formatValue(n.value)} ${valueLabel}`}</title>
            </text>
          </g>
        ))}
      </svg>

      {hover && (
        <div
          className={styles.tooltip}
          style={{ left: `${Math.min(hover.x + 12, 640)}px`, top: `${hover.y + 12}px` }}
        >
          <Text size={200} weight="semibold" className={styles.tipLine}>
            {model.ribbons[hover.i].flow.sourceLabel} {'\u2192'} {model.ribbons[hover.i].flow.targetLabel}
          </Text>
          <Text size={200} className={styles.tipLine}>
            {formatValue(model.ribbons[hover.i].flow.value)} {valueLabel}
          </Text>
          {model.ribbons[hover.i].flow.detail?.map((d) => (
            <Text size={200} key={d} className={styles.tipLine}>
              {d}
            </Text>
          ))}
        </div>
      )}

      {caption && (
        <Text size={200} className={styles.caption}>
          {caption}
        </Text>
      )}

      {/* The diagram is decorative to a screen reader; this is the same data as text. */}
      <table className={styles.srOnly}>
        <caption>Where visits started and ended</caption>
        <thead>
          <tr>
            <th scope="col">Started on</th>
            <th scope="col">Ended on</th>
            <th scope="col">{valueLabel}</th>
          </tr>
        </thead>
        <tbody>
          {model.ribbons.map((r) => (
            <tr key={`sr-${r.flow.sourceLabel}\u0000${r.flow.targetLabel}`}>
              <td>{r.flow.sourceLabel}</td>
              <td>{r.flow.isSelfFlow ? 'the same page' : r.flow.targetLabel}</td>
              <td>{formatValue(r.flow.value)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function truncate(label: string): string {
  return label.length > 28 ? `${label.slice(0, 27)}\u2026` : label;
}

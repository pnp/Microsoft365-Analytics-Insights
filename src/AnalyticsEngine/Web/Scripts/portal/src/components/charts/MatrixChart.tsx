import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { ReportMatrix } from '../../types/reports';
import { useT } from '../../i18n';
import { serverPlaceholderText } from '../shared/serverPlaceholder';
import { formatValue, formatCompact } from './chartCommon';

type MatrixChartProps = {
  matrix: ReportMatrix;
  /** Unit for the cell tooltip, e.g. "People". */
  valueLabel: string;
};

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    width: '100%',
  },
  // The grid is allowed to overflow horizontally rather than squeezing 20 columns into the card.
  // Compressed to fit, the numbers become unreadable and the whole point of showing them is lost.
  scroller: {
    width: '100%',
    overflowX: 'auto',
  },
  table: {
    borderCollapse: 'separate',
    borderSpacing: '2px',
    // Fixed layout stops one long department name stretching its column far wider than the rest.
    tableLayout: 'fixed',
  },
  corner: {
    color: tokens.colorNeutralForeground3,
    fontSize: '11px',
    fontWeight: tokens.fontWeightSemibold,
    textAlign: 'left',
    verticalAlign: 'bottom',
    paddingInlineEnd: '8px',
    width: '104px',
    minWidth: '104px',
  },
  columnHead: {
    color: tokens.colorNeutralForeground3,
    fontSize: '11px',
    fontWeight: tokens.fontWeightRegular,
    textAlign: 'center',
    verticalAlign: 'bottom',
    paddingBottom: '4px',
    width: '72px',
    minWidth: '72px',
    maxWidth: '72px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  rowHead: {
    color: tokens.colorNeutralForeground2,
    fontSize: '12px',
    fontWeight: tokens.fontWeightRegular,
    textAlign: 'left',
    paddingInlineEnd: '8px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  cell: {
    height: '30px',
    borderRadius: '3px',
    textAlign: 'center',
    fontSize: '11px',
    fontVariantNumeric: 'tabular-nums',
  },
  legend: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
  },
  swatch: {
    width: '14px',
    height: '14px',
    borderRadius: '3px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
});

/**
 * A shaded grid for two categorical dimensions - "which app, in which department".
 *
 * Deliberate choices, all of them the same ones {@link HeatmapChart} makes and for the same
 * reasons:
 *
 * 1. **The number is printed in the cell, not just the shade.** This grid is read by people who
 *    have to act on it, and "Finance uses Excel more than Field Ops" is not actionable until you
 *    know whether that is 400 people or 4.
 * 2. **A zero cell is the neutral background, never the faintest shade.** "Nobody" and "almost
 *    nobody" are different findings and must not look the same.
 * 3. **Intensity scales with the square root of the value**, so a single dominant cell does not
 *    wash every other cell out to blank.
 * 4. **Optional per-row shading** (`shadeByRow`). Outlook is used by nearly everyone and OneNote by
 *    a small minority; on one shared scale the entire OneNote row renders blank and the chart
 *    silently stops answering "who uses OneNote most", which is the only reason that row exists.
 *
 * There are deliberately **no row or column totals**. For some matrices a total would be a plain
 * sum of the cells, but for others it would not: a person using Word on both Windows and the web
 * appears in two cells of the Word row, so summing that row would report more Word users than
 * exist. Rather than print a total that is right on one grid and wrong on another, none is shown.
 */
export default function MatrixChart({ matrix, valueLabel }: MatrixChartProps) {
  const t = useT();
  const styles = useStyles();

  const { rows, columns, cells, rowLabel, columnLabel, shadeByRow } = matrix;

  const values = new Map<string, number>();
  let total = 0;
  for (const cell of cells) {
    const key = `${cell.row}\u0000${cell.column}`;
    const next = (values.get(key) ?? 0) + cell.value;
    values.set(key, next);
    total += cell.value;
  }

  if (rows.length === 0 || columns.length === 0 || total <= 0) {
    return <div className={styles.empty}>{t('charts.empty.noDataForPeriod')}</div>;
  }

  const valueAt = (row: string, column: string): number => values.get(`${row}\u0000${column}`) ?? 0;

  const gridMax = Math.max(...rows.flatMap((r) => columns.map((c) => valueAt(r, c))), 0);
  const rowMax = new Map<string, number>(
    rows.map((r) => [r, Math.max(...columns.map((c) => valueAt(r, c)), 0)]),
  );

  return (
    <div className={styles.root}>
      <div className={styles.scroller}>
        <table className={styles.table}>
          <thead>
            <tr>
              <th className={styles.corner} scope="col">
                {rowLabel}
              </th>
              {columns.map((column) => (
                <th key={column} className={styles.columnHead} scope="col" title={serverPlaceholderText(t, column)}>
                  {serverPlaceholderText(t, column)}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => {
              const max = shadeByRow ? (rowMax.get(row) ?? 0) : gridMax;
              const rowText = serverPlaceholderText(t, row);
              return (
                <tr key={row}>
                  <th className={styles.rowHead} scope="row" title={rowText}>
                    {rowText}
                  </th>
                  {columns.map((column) => {
                    const value = valueAt(row, column);
                    return (
                      <td
                        key={column}
                        className={styles.cell}
                        style={{ backgroundColor: shade(value, max), color: textColour(value, max) }}
                        title={t('charts.matrix.cellTitle', { row: rowText, column: serverPlaceholderText(t, column), value: formatValue(value), valueLabel })}
                      >
                        {value > 0 ? formatCompact(value) : ''}
                      </td>
                    );
                  })}
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      <div className={styles.legend}>
        <Text size={100} className={styles.muted}>
          {t(shadeByRow ? 'charts.matrix.legendShadedByRow' : 'charts.matrix.legendShadedOverall', {
            columnLabel,
          })}
        </Text>
        <span className={styles.swatch} style={{ backgroundColor: shade(0, 1) }} />
        {[0.25, 0.5, 0.75, 1].map((fraction) => (
          <span key={fraction} className={styles.swatch} style={{ backgroundColor: shade(fraction, 1) }} />
        ))}
        <Text size={100} className={styles.muted}>
          {t('charts.matrix.most')}
        </Text>
      </div>
    </div>
  );
}

/**
 * The fill for a cell. Zero is the neutral surface; everything else is a single-hue ramp on the
 * brand blue, scaled by the square root of the value so light activity stays visible.
 */
function shade(value: number, max: number): string {
  if (value <= 0) return tokens.colorNeutralBackground3;

  const intensity = Math.sqrt(Math.min(1, value / Math.max(max, 1)));
  const alpha = 0.15 + intensity * 0.85;
  return `rgba(15, 108, 189, ${alpha.toFixed(3)})`;
}

/** White text on the dark end of the ramp, normal foreground elsewhere, so the number stays legible. */
function textColour(value: number, max: number): string {
  if (value <= 0) return tokens.colorNeutralForeground3;
  const intensity = Math.sqrt(Math.min(1, value / Math.max(max, 1)));
  return intensity > 0.6 ? tokens.colorNeutralForegroundInverted : tokens.colorNeutralForeground1;
}

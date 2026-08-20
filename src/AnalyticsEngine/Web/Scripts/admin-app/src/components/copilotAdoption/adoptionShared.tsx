import { makeStyles, tokens, Text, Badge } from '@fluentui/react-components';
import { AdoptionBand } from '../../types/copilotAdoption';
import type { AdoptionSegmentRow } from '../../types/copilotAdoption';
import { formatCount, formatPct } from './KpiGrid';

/**
 * Band colours run cold-to-warm with maturity, and the two zero-usage bands are deliberately the
 * only red ones - they are the seats that are costing money for nothing.
 */
const BAND_COLOUR: Record<AdoptionBand, string> = {
  [AdoptionBand.NeverUsed]: '#d13438',
  [AdoptionBand.Dormant]: '#ca5010',
  [AdoptionBand.Trialling]: '#c19c00',
  [AdoptionBand.Developing]: '#0f6cbd',
  [AdoptionBand.Established]: '#008272',
  [AdoptionBand.Champion]: '#107c10',
};

const useStyles = makeStyles({
  badge: {
    color: tokens.colorNeutralForegroundOnBrand,
    whiteSpace: 'nowrap',
  },
  scoreCell: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    minWidth: '108px',
  },
  scoreTrack: {
    position: 'relative',
    flexGrow: 1,
    height: '8px',
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralBackground3,
    overflow: 'hidden',
    minWidth: '46px',
  },
  scoreBar: {
    height: '100%',
    borderRadius: tokens.borderRadiusSmall,
  },
  scoreValue: {
    fontVariantNumeric: 'tabular-nums',
    minWidth: '34px',
    textAlign: 'right',
  },
  table: {
    width: '100%',
    borderCollapse: 'collapse',
  },
  th: {
    textAlign: 'left',
    padding: '6px 10px',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke2,
    color: tokens.colorNeutralForeground3,
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
  },
  thNumeric: {
    textAlign: 'right',
  },
  td: {
    padding: '6px 10px',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke3,
    verticalAlign: 'middle',
  },
  tdNumeric: {
    textAlign: 'right',
    fontVariantNumeric: 'tabular-nums',
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '20px 0',
    textAlign: 'center',
  },
});

/** The engagement band as a coloured pill. */
export function BandBadge({ band, name }: { band: AdoptionBand; name: string }) {
  const styles = useStyles();
  return (
    <Badge className={styles.badge} style={{ backgroundColor: BAND_COLOUR[band] ?? '#605e5c' }} size="small">
      {name}
    </Badge>
  );
}

/**
 * A 0-100 score as a small inline bar plus the number.
 *
 * The bar matters: a column of bare numbers makes an executive read every row, whereas a column of
 * bars makes the shape of the problem visible in one glance - which is the whole reason this list
 * exists rather than a raw data dump.
 */
export function ScoreBar({ score, colour }: { score: number; colour?: string }) {
  const styles = useStyles();
  const clamped = Math.max(0, Math.min(100, score));

  return (
    <div className={styles.scoreCell}>
      <div className={styles.scoreTrack}>
        <div
          className={styles.scoreBar}
          style={{ width: `${clamped}%`, backgroundColor: colour ?? scoreColour(clamped) }}
        />
      </div>
      <Text size={200} weight="semibold" className={styles.scoreValue}>
        {Math.round(clamped)}
      </Text>
    </div>
  );
}

/** Score-to-colour, using the same thresholds as the engagement bands so the two never disagree. */
export function scoreColour(score: number): string {
  if (score >= 75) return BAND_COLOUR[AdoptionBand.Champion];
  if (score >= 50) return BAND_COLOUR[AdoptionBand.Established];
  if (score >= 25) return BAND_COLOUR[AdoptionBand.Developing];
  if (score > 0) return BAND_COLOUR[AdoptionBand.Trialling];
  return BAND_COLOUR[AdoptionBand.NeverUsed];
}

/**
 * Adoption per department or country, worst first.
 *
 * Shows the seat count next to the percentage on purpose: "0% adopted" across six seats and across
 * six hundred are the same percentage and completely different decisions, and a chart that shows
 * only the rate invites the wrong one.
 */
export function SegmentTable({ rows, segmentLabel }: { rows: AdoptionSegmentRow[]; segmentLabel: string }) {
  const styles = useStyles();

  if (rows.length === 0) {
    return (
      <div className={styles.empty}>
        Not enough licensed users in any {segmentLabel.toLowerCase()} to break down reliably.
      </div>
    );
  }

  return (
    <table className={styles.table}>
      <thead>
        <tr>
          <th className={styles.th}>{segmentLabel}</th>
          <th className={`${styles.th} ${styles.thNumeric}`}>Seats</th>
          <th className={`${styles.th} ${styles.thNumeric}`}>Active</th>
          <th className={`${styles.th} ${styles.thNumeric}`}>Habitual</th>
          <th className={`${styles.th} ${styles.thNumeric}`}>Never used</th>
          <th className={styles.th}>Adoption rate</th>
          <th className={styles.th}>Avg. score</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((row) => (
          <tr key={row.segment}>
            <td className={styles.td}>{row.segment}</td>
            <td className={`${styles.td} ${styles.tdNumeric}`}>{formatCount(row.licensedUsers)}</td>
            <td className={`${styles.td} ${styles.tdNumeric}`}>{formatCount(row.activeUsers)}</td>
            <td className={`${styles.td} ${styles.tdNumeric}`}>{formatCount(row.habitualUsers)}</td>
            <td className={`${styles.td} ${styles.tdNumeric}`}>{formatCount(row.neverUsedUsers)}</td>
            <td className={styles.td}>
              <div className={styles.scoreCell}>
                <div className={styles.scoreTrack}>
                  <div
                    className={styles.scoreBar}
                    style={{
                      width: `${Math.max(0, Math.min(100, row.adoptionRatePct))}%`,
                      backgroundColor: scoreColour(row.adoptionRatePct),
                    }}
                  />
                </div>
                <Text size={200} weight="semibold" className={styles.scoreValue}>
                  {formatPct(row.adoptionRatePct)}
                </Text>
              </div>
            </td>
            <td className={styles.td}>
              <ScoreBar score={row.averageAdoptionScore} />
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/** Shared table styling for the two user lists, so they look and behave identically. */
export function useAdoptionTableStyles() {
  return useStyles();
}

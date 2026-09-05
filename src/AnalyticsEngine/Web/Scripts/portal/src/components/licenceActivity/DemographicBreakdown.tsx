import { memo, useMemo } from 'react';
import { makeStyles, tokens, Card, Text, MessageBar, MessageBarBody } from '@fluentui/react-components';
import type { LicenceActivityDemographic, LicenceActivityDistribution } from '../../types/licenceActivity';
import { WORKLOADS } from '../../types/licenceActivity';
import { formatCount } from './format';
import { useLaTableStyles } from './tableStyles';
import { MiniDistribution, BandLegend } from './MiniDistribution';

/** Hard client-side cap so a tenant with hundreds of departments can't render an unbounded table.
 *  The backend also truncates (demographicsTruncated); either way the note below makes it explicit. */
const MAX_ROWS = 50;

const EMPTY: LicenceActivityDistribution = { workload: '', high: 0, moderate: 0, low: 0, zero: 0, unknown: 0 };

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
  },
  head: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  scroll: {
    maxHeight: '360px',
    overflowY: 'auto',
  },
  stickyHead: {
    position: 'sticky',
    insetBlockStart: 0,
    backgroundColor: tokens.colorNeutralBackground1,
    zIndex: 1,
  },
});

interface DemographicBreakdownProps {
  title: string;
  segmentLabel: string;
  rows: LicenceActivityDemographic[];
  /** The backend's demographicsTruncated flag - true when its own list was capped. */
  truncated: boolean;
}

/**
 * An aggregate breakdown of a demographic dimension (department or country): assigned users and the
 * five workload distributions per segment, straight from the overview DTO - not merely the filter
 * option list. Sorted largest-first, capped and scroll-bounded so it stays responsive at ~50 segments
 * on a 300k-user tenant, with truncation made explicit.
 */
function DemographicBreakdown({ title, segmentLabel, rows, truncated }: DemographicBreakdownProps) {
  const styles = useStyles();
  const table = useLaTableStyles();

  const sorted = useMemo(
    () => [...rows].sort((a, b) => b.assignedUsers - a.assignedUsers || a.name.localeCompare(b.name)),
    [rows],
  );
  const shown = sorted.slice(0, MAX_ROWS);
  const capped = truncated || sorted.length > MAX_ROWS;

  if (rows.length === 0) return null;

  return (
    <Card className={styles.card}>
      <div className={styles.head}>
        <Text weight="semibold" size={400}>
          {title}
        </Text>
        <Text size={200} className={styles.muted}>
          Assigned users and per-workload activity by {segmentLabel.toLowerCase()}, largest first.
        </Text>
        <BandLegend />
      </div>

      {capped && (
        <MessageBar intent="info">
          <MessageBarBody>
            Showing the top {formatCount(shown.length)} {segmentLabel.toLowerCase()} by assigned users; the full list
            is truncated.
          </MessageBarBody>
        </MessageBar>
      )}

      <div className={`${table.wrap} ${styles.scroll}`}>
        <table className={table.table}>
          <thead className={styles.stickyHead}>
            <tr>
              <th className={table.th}>{segmentLabel}</th>
              <th className={`${table.th} ${table.thNumeric}`}>Assigned</th>
              {WORKLOADS.map((w) => (
                <th key={w.key} className={table.th}>
                  {w.label}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {shown.map((seg) => (
              <tr key={seg.id}>
                <td className={table.td}>{seg.name}</td>
                <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(seg.assignedUsers)}</td>
                {WORKLOADS.map((w) => {
                  const dist = seg.workloads.find((d) => d.workload === w.key) ?? { ...EMPTY, workload: w.key };
                  return (
                    <td key={w.key} className={table.td}>
                      <MiniDistribution distribution={dist} />
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Card>
  );
}

// Memoised: renders up to 50 rows x 5 mini-bars and should not re-render on unrelated page state.
export default memo(DemographicBreakdown);

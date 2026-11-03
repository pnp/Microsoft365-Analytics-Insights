import { memo, useMemo } from 'react';
import { makeStyles, tokens, Card, Text, MessageBar, MessageBarBody } from '@fluentui/react-components';
import type { LicenceActivityDemographic, LicenceActivityDistribution } from '../../types/licenceActivity';
import { WORKLOADS } from '../../types/licenceActivity';
import { compareStrings, plural, useT } from '../../i18n';
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
  /** How many groups the server combined into the `other` row, for a reader without See PII. */
  otherGroups?: number;
  /** The fewest people a row describes for a reader without See PII; null or absent when every group is shown. */
  minimumGroupSize?: number | null;
}

export function demographicName(
  t: ReturnType<typeof useT>,
  row: Pick<LicenceActivityDemographic, 'id' | 'name' | 'other'>,
  otherGroups = 0,
): string {
  if (row.other) {
    return t(plural(otherGroups, 'licenceActivity.demographics.otherRow.one', 'licenceActivity.demographics.otherRow.other'), {
      count: formatCount(otherGroups),
    });
  }
  return row.id === 0 ? t('licenceActivity.demographics.unknownBucket') : row.name ?? '';
}

/**
 * An aggregate breakdown of a demographic dimension (department or country): assigned users and the
 * five workload distributions per segment, straight from the overview DTO - not merely the filter
 * option list. Sorted largest-first, capped and scroll-bounded so it stays responsive at ~50 segments
 * on a 300k-user tenant, with truncation made explicit. For a reader without See PII the server has
 * already combined the groups too small to name into one row, which is listed last.
 */
function DemographicBreakdown({ title, segmentLabel, rows, truncated, otherGroups = 0, minimumGroupSize }: DemographicBreakdownProps) {
  const styles = useStyles();
  const table = useLaTableStyles();
  const t = useT();

  const sorted = useMemo(
    () =>
      [...rows].sort(
        (a, b) =>
          Number(a.other ?? false) - Number(b.other ?? false) ||
          b.assignedUsers - a.assignedUsers ||
          compareStrings(a.name ?? '', b.name ?? ''),
      ),
    [rows],
  );
  const shown = sorted.slice(0, MAX_ROWS);
  const capped = truncated || sorted.length > MAX_ROWS;
  const combinedNote =
    rows.some((row) => row.other) && minimumGroupSize != null
      ? t('licenceActivity.demographics.otherNote', { count: formatCount(minimumGroupSize) })
      : null;

  if (rows.length === 0) return null;

  return (
    <Card className={styles.card}>
      <div className={styles.head}>
        <Text weight="semibold" size={400}>
          {title}
        </Text>
        <Text size={200} className={styles.muted}>
          {t('licenceActivity.demographics.description', { segment: segmentLabel.toLowerCase() })}
        </Text>
        {combinedNote && (
          <Text size={200} className={styles.muted}>
            {combinedNote}
          </Text>
        )}
        <BandLegend />
      </div>

      {capped && (
        <MessageBar intent="info">
          <MessageBarBody>
            {t('licenceActivity.demographics.capped', { count: formatCount(shown.length) })}
          </MessageBarBody>
        </MessageBar>
      )}

      <div className={`${table.wrap} ${styles.scroll}`}>
        <table className={table.table}>
          <thead className={styles.stickyHead}>
            <tr>
              <th className={table.th}>{segmentLabel}</th>
              <th className={`${table.th} ${table.thNumeric}`}>{t('licenceActivity.common.peopleAssigned')}</th>
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
                <td className={table.td}>{demographicName(t, seg, otherGroups)}</td>
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

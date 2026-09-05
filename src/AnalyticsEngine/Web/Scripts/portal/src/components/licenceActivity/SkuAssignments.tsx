import { memo, useMemo, useState, type CSSProperties } from 'react';
import { makeStyles, tokens, Card, Text, Input } from '@fluentui/react-components';
import type { LicenceActivityDistribution, LicenceActivitySku } from '../../types/licenceActivity';
import { WORKLOADS } from '../../types/licenceActivity';
import { formatCount, licenceName } from './format';
import { useLaTableStyles } from './tableStyles';
import { MiniDistribution, BandLegend } from './MiniDistribution';

/** Above this many SKUs, show the filter box and cap the table height so the selector stays scannable. */
const FILTER_THRESHOLD = 8;

const EMPTY: LicenceActivityDistribution = { workload: '', high: 0, moderate: 0, low: 0, zero: 0, unknown: 0 };

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
  },
  head: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
  },
  headText: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  filter: {
    minWidth: '220px',
  },
  sku: {
    display: 'flex',
    flexDirection: 'column',
  },
  // Cap the height so 50 SKUs scroll within the card instead of pushing the workload distributions
  // and drill-down far down the page. The sticky header keeps the columns visible while scrolling.
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
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '16px 0',
  },
});

interface SkuAssignmentsProps {
  licences: LicenceActivitySku[];
  selectedLicenceTypeId: number | null;
  onSelect: (licenceTypeId: number) => void;
}

// A visually-seamless reset so the real <button> fills the cell without its default chrome. Kept as a
// plain inline style to sidestep Griffel's shorthand restrictions (border/background/font). The native
// focus outline is intentionally NOT removed, so keyboard focus stays visible.
const selectButtonStyle: CSSProperties = {
  display: 'block',
  width: '100%',
  textAlign: 'left',
  background: 'transparent',
  border: 'none',
  padding: '2px 0',
  margin: 0,
  font: 'inherit',
  color: 'inherit',
  cursor: 'pointer',
};

/**
 * The executive licence view: one row per SKU with how many users hold it. Selecting a row drives the
 * workload distributions and (for users with the role) the per-user drill-down.
 *
 * Built to stay responsive at ~50 SKUs of very uneven size (some tenant-wide, some with a handful of
 * seats): the list is sorted by assigned users so the biggest are first, a filter narrows it by name
 * or SKU id, and the body scrolls within a bounded height. Rendering is O(SKUs) - the heavy per-user
 * work stays server-side and paged.
 */
function SkuAssignments({ licences, selectedLicenceTypeId, onSelect }: SkuAssignmentsProps) {
  const styles = useStyles();
  const table = useLaTableStyles();
  const [filter, setFilter] = useState('');

  const sorted = useMemo(
    () => [...licences].sort((a, b) => b.assignedUsers - a.assignedUsers || licenceName(a).localeCompare(licenceName(b))),
    [licences],
  );

  const visible = useMemo(() => {
    const q = filter.trim().toLocaleLowerCase();
    if (!q) return sorted;
    return sorted.filter(
      (s) => licenceName(s).toLocaleLowerCase().includes(q) || (s.skuId ?? '').toLocaleLowerCase().includes(q),
    );
  }, [sorted, filter]);

  const showFilter = licences.length > FILTER_THRESHOLD;

  if (licences.length === 0) {
    return (
      <Card className={styles.card}>
        <Text className={styles.muted}>No licence assignments were found for this scope.</Text>
      </Card>
    );
  }

  return (
    <Card className={styles.card}>
      <div className={styles.head}>
        <div className={styles.headText}>
          <Text weight="semibold" size={400}>
            Licence assignments
          </Text>
          <Text size={200} className={styles.muted}>
            Select a licence to see its workload activity and, with the required role, who holds it.
            {showFilter ? ` Showing ${formatCount(visible.length)} of ${formatCount(licences.length)}.` : ''}
          </Text>
        </div>
        {showFilter && (
          <Input
            className={styles.filter}
            value={filter}
            placeholder="Filter by name or SKU"
            aria-label="Filter licences"
            onChange={(_e, d) => setFilter(d.value)}
          />
        )}
      </div>

      <BandLegend />

      <div className={`${table.wrap} ${showFilter ? styles.scroll : ''}`.trim()}>
        <table className={table.table}>
          <thead className={showFilter ? styles.stickyHead : undefined}>
            <tr>
              <th className={table.th}>Licence</th>
              <th className={`${table.th} ${table.thNumeric}`}>Assigned users</th>
              {WORKLOADS.map((w) => (
                <th key={w.key} className={table.th}>
                  {w.label}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {visible.map((sku) => {
              const selected = sku.licenceTypeId === selectedLicenceTypeId;
              return (
                <tr
                  key={sku.licenceTypeId}
                  className={`${table.selectableRow} ${selected ? table.selectedRow : ''}`.trim()}
                  // `selectableRow` puts a pointer cursor and a hover highlight on the WHOLE row, but
                  // activation lives in the button in the first cell - so the assigned-users cell and
                  // the five distribution cells (most of the row's width) advertised a click target
                  // that did nothing. Make the advertisement true for the mouse rather than removing
                  // the useful hover cue. This adds no ARIA role, so the native row/cell semantics
                  // the a11y tests pin are untouched, and the button remains the only control exposed
                  // to assistive technology and the keyboard.
                  onClick={() => onSelect(sku.licenceTypeId)}
                >
                  <td className={table.td}>
                    {/* Activation lives in a real button (keyboard-operable, correctly announced),
                        so the <tr> keeps native table-row semantics instead of role="button", which
                        would swallow the row/cell roles and read every cell as one giant label. */}
                    <button
                      type="button"
                      style={selectButtonStyle}
                      aria-pressed={selected}
                      onClick={(e) => {
                        // The row handler would otherwise fire too; harmless (same id) but avoid the
                        // duplicate call so onSelect stays once-per-activation for callers and tests.
                        e.stopPropagation();
                        onSelect(sku.licenceTypeId);
                      }}
                    >
                      <span className={styles.sku}>
                        <Text size={300} weight="semibold">
                          {licenceName(sku)}
                        </Text>
                        {sku.skuId && sku.skuId !== licenceName(sku) && (
                          <Text size={100} className={styles.muted}>
                            {sku.skuId}
                          </Text>
                        )}
                      </span>
                    </button>
                  </td>
                  <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(sku.assignedUsers)}</td>
                  {WORKLOADS.map((w) => {
                    const dist = sku.workloads.find((d) => d.workload === w.key) ?? { ...EMPTY, workload: w.key };
                    return (
                      <td key={w.key} className={table.td}>
                        <MiniDistribution distribution={dist} />
                      </td>
                    );
                  })}
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      {visible.length === 0 && <Text className={styles.empty}>No licences match &ldquo;{filter}&rdquo;.</Text>}
    </Card>
  );
}

// Memoised: the page re-renders when unrelated state changes (users snapshot id, export state), and
// this list of up to 50 rows should not re-render unless the licences or the selection actually change.
export default memo(SkuAssignments);

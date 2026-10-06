import { memo, useMemo, useState, type CSSProperties, type ReactNode } from 'react';
import { makeStyles, tokens, Card, Text, Input, Switch } from '@fluentui/react-components';
import { ArrowSort16Regular, ArrowSortDown16Regular, ArrowSortUp16Regular } from '@fluentui/react-icons';
import type {
  LicenceActivityAllLicences,
  LicenceActivityDistribution,
  LicenceActivitySku,
  WorkloadKey,
} from '../../types/licenceActivity';
import { WORKLOADS } from '../../types/licenceActivity';
import { compareStrings, useT } from '../../i18n';
import { DASH, formatCount, formatPct, licenceName } from './format';
import { activeRatePct } from './bands';
import { useLaTableStyles } from './tableStyles';
import { MiniDistribution, BandLegend } from './MiniDistribution';
import { ALL_LICENCES, distributionFor, type LicenceScope } from './scope';
import AdoptionScoreBar, { AdoptionScoreInfo } from './AdoptionScoreBar';

/** Above this many licences, show the filter box and cap the table height so the list stays scannable. */
const FILTER_THRESHOLD = 8;

const EMPTY: LicenceActivityDistribution = { workload: '', high: 0, moderate: 0, low: 0, zero: 0, unknown: 0 };

export type LicenceSortKey = 'name' | 'people' | 'score' | WorkloadKey;
export type LicenceSortDirection = 'asc' | 'desc';
export interface LicenceSort {
  key: LicenceSortKey;
  direction: LicenceSortDirection;
}

/** Biggest first, as the table has always opened. */
export const DEFAULT_LICENCE_SORT: LicenceSort = { key: 'people', direction: 'desc' };

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
    maxWidth: '720px',
  },
  tools: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    flexWrap: 'wrap',
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
    minWidth: '180px',
  },
  // Bound the height so 50 licences scroll inside the card instead of pushing the service detail far
  // down the page. The sticky header keeps the sortable columns in view while scrolling.
  scroll: {
    maxHeight: '440px',
    overflowY: 'auto',
  },
  stickyHead: {
    position: 'sticky',
    insetBlockStart: 0,
    backgroundColor: tokens.colorNeutralBackground1,
    zIndex: 1,
  },
  sortButton: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    color: 'inherit',
    fontWeight: 'inherit',
    cursor: 'pointer',
  },
  workloadCell: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  rate: {
    minWidth: '38px',
    textAlign: 'right',
    fontVariantNumeric: 'tabular-nums',
    color: tokens.colorNeutralForeground2,
  },
  baselineRow: {
    backgroundColor: tokens.colorNeutralBackground2,
  },
  baselineName: {
    color: tokens.colorBrandForeground1,
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '16px 0',
  },
});

// A visually-seamless reset so a real <button> fills a cell without its default chrome. Kept as an
// inline style to sidestep Griffel's shorthand restrictions (border/background/font). The native focus
// outline is intentionally NOT removed, so keyboard focus stays visible.
const bareButtonStyle: CSSProperties = {
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

const headerButtonStyle: CSSProperties = { ...bareButtonStyle, display: 'inline-flex', width: 'auto', padding: 0 };

function workloadRate(sku: LicenceActivitySku, workload: WorkloadKey): number | null {
  const d = distributionFor(sku.workloads, workload);
  return d ? activeRatePct(d) : null;
}

/** Compares two possibly-unknown numbers so that unknown always sorts last, whichever the direction. */
function compareKnownFirst(a: number | null, b: number | null, direction: LicenceSortDirection): number {
  if (a == null) return b == null ? 0 : 1;
  if (b == null) return -1;
  return direction === 'asc' ? a - b : b - a;
}

/** Sorts licences for the table. Exported for tests. */
export function sortLicences(licences: LicenceActivitySku[], sort: LicenceSort): LicenceActivitySku[] {
  const byName = (a: LicenceActivitySku, b: LicenceActivitySku) => compareStrings(licenceName(a), licenceName(b));
  const byPeople = (a: LicenceActivitySku, b: LicenceActivitySku) => b.assignedUsers - a.assignedUsers;
  return [...licences].sort((a, b) => {
    let compared: number;
    switch (sort.key) {
      case 'name':
        compared = sort.direction === 'asc' ? byName(a, b) : byName(b, a);
        break;
      case 'people':
        compared = sort.direction === 'asc' ? a.assignedUsers - b.assignedUsers : b.assignedUsers - a.assignedUsers;
        break;
      case 'score':
        compared = compareKnownFirst(a.adoptionScore ?? null, b.adoptionScore ?? null, sort.direction);
        break;
      default:
        compared = compareKnownFirst(workloadRate(a, sort.key), workloadRate(b, sort.key), sort.direction);
    }
    return compared || byPeople(a, b) || byName(a, b) || a.licenceTypeId - b.licenceTypeId;
  });
}

interface LicenceComparisonTableProps {
  licences: LicenceActivitySku[];
  /** Everyone holding a licence: the baseline row. Null when the server sent no all-licence figures. */
  allLicences: LicenceActivityAllLicences | null;
  selected: LicenceScope;
  onSelect: (scope: LicenceScope) => void;
}

/**
 * Every licence side by side, so they can be compared rather than read one at a time: how many people
 * hold each, its adoption score, and the share of its measured holders active in each service.
 *
 * - Every column sorts (licence name, people, adoption score, and each service's active share), so the
 *   most and least used licences for any service are one click away.
 * - "Everyone holding a licence" leads the table as the baseline every licence is read against.
 * - Licences nobody holds are hidden by default - they have nothing to compare - with a switch to show
 *   them.
 * - Selecting a row (anywhere on it, or with the keyboard via the button in its first cell) shows that
 *   licence's services and people below, without leaving the tab.
 *
 * Built to stay responsive at ~50 licences of very uneven size: rendering is O(licences); the per-person
 * work stays on the server.
 */
function LicenceComparisonTable({ licences, allLicences, selected, onSelect }: LicenceComparisonTableProps) {
  const styles = useStyles();
  const table = useLaTableStyles();
  const t = useT();
  const [filter, setFilter] = useState('');
  const [hideEmpty, setHideEmpty] = useState(true);
  const [sort, setSort] = useState<LicenceSort>(DEFAULT_LICENCE_SORT);

  const emptyCount = useMemo(() => licences.filter((l) => l.assignedUsers === 0).length, [licences]);

  const visible = useMemo(() => {
    const q = filter.trim().toLocaleLowerCase();
    // A selected licence stays visible even when it would be hidden, so the selection never vanishes.
    const shown = licences.filter(
      (l) => !hideEmpty || l.assignedUsers > 0 || l.licenceTypeId === selected,
    );
    const matched = q
      ? shown.filter(
          (s) => licenceName(s).toLocaleLowerCase().includes(q) || (s.skuId ?? '').toLocaleLowerCase().includes(q),
        )
      : shown;
    return sortLicences(matched, sort);
  }, [licences, filter, hideEmpty, sort, selected]);

  const showFilter = licences.length > FILTER_THRESHOLD;

  if (licences.length === 0) {
    return (
      <Card className={styles.card}>
        <Text className={styles.muted}>{t('licenceActivity.assignments.empty')}</Text>
      </Card>
    );
  }

  const onSort = (key: LicenceSortKey) =>
    setSort((current) =>
      current.key === key
        ? { key, direction: current.direction === 'asc' ? 'desc' : 'asc' }
        : // Names read A-Z first; every figure reads highest first.
          { key, direction: key === 'name' ? 'asc' : 'desc' },
    );

  const sortHeader = (key: LicenceSortKey, label: string, numeric = false, extra?: ReactNode) => {
    const active = sort.key === key;
    const icon = !active ? (
      <ArrowSort16Regular aria-hidden />
    ) : sort.direction === 'asc' ? (
      <ArrowSortUp16Regular aria-hidden />
    ) : (
      <ArrowSortDown16Regular aria-hidden />
    );
    return (
      <th
        key={key}
        className={`${table.th} ${numeric ? table.thNumeric : ''}`.trim()}
        aria-sort={active ? (sort.direction === 'asc' ? 'ascending' : 'descending') : 'none'}
      >
        <button
          type="button"
          style={headerButtonStyle}
          className={styles.sortButton}
          title={t('licenceActivity.compare.sortBy', { column: label })}
          onClick={() => onSort(key)}
        >
          {label}
          {icon}
        </button>
        {extra}
      </th>
    );
  };

  const nameCell = (scope: LicenceScope, name: ReactNode, caption: string | null, baseline = false) => (
    <td className={table.td}>
      {/* Activation lives in a real button (keyboard-operable, correctly announced), so the <tr> keeps
          native table-row semantics instead of role="button", which would read every cell as one label. */}
      <button
        type="button"
        style={bareButtonStyle}
        aria-pressed={selected === scope}
        onClick={(e) => {
          // The row handler would fire too; harmless (same scope) but keep onSelect once per activation.
          e.stopPropagation();
          onSelect(scope);
        }}
      >
        <span className={styles.sku}>
          <Text size={300} weight="semibold" className={baseline ? styles.baselineName : undefined}>
            {name}
          </Text>
          {caption && (
            <Text size={100} className={styles.muted}>
              {caption}
            </Text>
          )}
        </span>
      </button>
    </td>
  );

  const workloadCells = (workloads: LicenceActivityDistribution[]) =>
    WORKLOADS.map((w) => {
      const dist = distributionFor(workloads, w.key) ?? { ...EMPTY, workload: w.key };
      const rate = activeRatePct(dist);
      return (
        <td key={w.key} className={table.td}>
          <span className={styles.workloadCell}>
            <MiniDistribution distribution={dist} width={72} />
            <Text
              size={200}
              className={styles.rate}
              title={
                rate == null
                  ? t('licenceActivity.common.notMeasured')
                  : t('licenceActivity.compare.rateTitle', { rate: formatPct(rate), service: w.label })
              }
            >
              {rate == null ? DASH : formatPct(rate)}
            </Text>
          </span>
        </td>
      );
    });

  return (
    <Card className={styles.card}>
      <div className={styles.head}>
        <div className={styles.headText}>
          <Text weight="semibold" size={400}>
            {t('licenceActivity.compare.title')}
          </Text>
          <Text size={200} className={styles.muted}>
            {t('licenceActivity.compare.description')}
            {visible.length !== licences.length
              ? ` ${t('licenceActivity.assignments.showingOf', {
                  shown: formatCount(visible.length),
                  total: formatCount(licences.length),
                })}`
              : ''}
          </Text>
        </div>
        <div className={styles.tools}>
          {emptyCount > 0 && (
            <Switch
              checked={hideEmpty}
              onChange={(_e, d) => setHideEmpty(d.checked)}
              label={t('licenceActivity.compare.hideEmpty', { count: formatCount(emptyCount) })}
            />
          )}
          {showFilter && (
            <Input
              className={styles.filter}
              value={filter}
              placeholder={t('licenceActivity.assignments.filterPlaceholder')}
              aria-label={t('licenceActivity.assignments.filterAria')}
              onChange={(_e, d) => setFilter(d.value)}
            />
          )}
        </div>
      </div>

      <BandLegend />

      <div className={`${table.wrap} ${showFilter ? styles.scroll : ''}`.trim()}>
        <table className={table.table}>
          <thead className={showFilter ? styles.stickyHead : undefined}>
            <tr>
              {sortHeader('name', t('licenceActivity.common.licence'))}
              {sortHeader('people', t('licenceActivity.common.peopleAssigned'), true)}
              {sortHeader('score', t('licenceActivity.compare.adoptionScore'), false, <AdoptionScoreInfo />)}
              {WORKLOADS.map((w) => sortHeader(w.key, w.label))}
            </tr>
          </thead>
          <tbody>
            {allLicences && (
              <tr
                className={`${table.selectableRow} ${styles.baselineRow} ${selected === ALL_LICENCES ? table.selectedRow : ''}`.trim()}
                onClick={() => onSelect(ALL_LICENCES)}
              >
                {nameCell(ALL_LICENCES, t('licenceActivity.scope.allLicensedUsers'), t('licenceActivity.scope.allLicensedUsersCaption'), true)}
                <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(allLicences.assignedUsers)}</td>
                <td className={table.td}>
                  <AdoptionScoreBar score={allLicences.adoptionScore} />
                </td>
                {workloadCells(allLicences.workloads)}
              </tr>
            )}
            {visible.map((sku) => (
              <tr
                key={sku.licenceTypeId}
                className={`${table.selectableRow} ${selected === sku.licenceTypeId ? table.selectedRow : ''}`.trim()}
                // The pointer cursor and hover highlight cover the whole row, so the whole row selects.
                // This adds no ARIA role: the button in the first cell stays the only control exposed
                // to assistive technology and the keyboard.
                onClick={() => onSelect(sku.licenceTypeId)}
              >
                {nameCell(
                  sku.licenceTypeId,
                  licenceName(sku),
                  sku.skuId && sku.skuId !== licenceName(sku) ? sku.skuId : null,
                )}
                <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(sku.assignedUsers)}</td>
                <td className={table.td}>
                  <AdoptionScoreBar score={sku.adoptionScore ?? null} />
                </td>
                {workloadCells(sku.workloads)}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {visible.length === 0 && filter.trim() !== '' && (
        <Text className={styles.empty}>{t('licenceActivity.assignments.noMatches', { filter })}</Text>
      )}
    </Card>
  );
}

// Memoised: the page re-renders when unrelated state changes (users snapshot id, export state), and this
// list of up to 50 rows should not re-render unless the licences or the selection actually change.
export default memo(LicenceComparisonTable);

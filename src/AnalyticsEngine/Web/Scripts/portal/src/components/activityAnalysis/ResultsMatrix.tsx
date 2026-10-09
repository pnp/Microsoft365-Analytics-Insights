import { Fragment, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Button, Card, Spinner, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import {
  ArrowSortDown16Regular,
  ArrowSortUp16Regular,
  ChevronDown16Regular,
  ChevronRight16Regular,
} from '@fluentui/react-icons';
import { compareStrings, formatNumber, plural, useT, type TFunction } from '../../i18n';
import { usePortalAccess } from '../../access';
import { fetchActivityAnalysisPeople } from '../../api/activityAnalysisApi';
import type {
  ActivityAnalysisDepartmentRow,
  ActivityAnalysisMetric,
  ActivityAnalysisMetricValue,
  ActivityAnalysisPeople,
  ActivityAnalysisQuery,
  ActivityAnalysisReport,
} from '../../types/activityAnalysis';
import InfoTip from '../shared/InfoTip';
import { groupName } from './ActivePeopleCharts';
import { formatMetricValue, metricLabel, metricLabelWithUnit } from './metrics';
import { useActivityTableStyles } from './tableStyles';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '14px 16px',
    minWidth: 0,
  },
  head: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
  },
  note: {
    color: tokens.colorNeutralForeground3,
  },
});

/** People shown under an expanded department - the API's default page, most active first. */
export const PEOPLE_PER_DEPARTMENT = 100;

/**
 * Departments drawn at a time. A large tenant can have thousands, each a row of two cells per selected
 * metric, so the rest wait behind "Show more" rather than all being drawn at once.
 */
export const DEPARTMENTS_PER_PAGE = 100;

type SortColumn = { kind: 'name' } | { kind: 'people' } | { kind: 'sum' | 'unique'; metric: string };
type SortState = { column: SortColumn; descending: boolean };

function sameColumn(a: SortColumn, b: SortColumn): boolean {
  return a.kind === b.kind && (a.kind === 'name' || a.kind === 'people' || a.metric === (b as { metric: string }).metric);
}

/** A row's identity: its name, with "not set" and the roll-up of small groups kept apart from any real name. */
export function departmentKey(row: Pick<ActivityAnalysisDepartmentRow, 'name' | 'other'>): string {
  if (row.other) return '\u0000other';
  return row.name == null ? '\u0000none' : `=${row.name}`;
}

function valueOf(row: ActivityAnalysisDepartmentRow, column: SortColumn): number {
  if (column.kind === 'people') return row.people;
  if (column.kind === 'name') return 0;
  const value = row.values.find((v) => v.metric === column.metric);
  return value ? value[column.kind] : 0;
}

/**
 * The departments in the order the reader asked for. The server's order (by name, "not set" last) is
 * the default; the roll-up of small groups always stays at the foot, and "not set" stays below the
 * named departments when sorting by name, so neither is mistaken for a real department in the list.
 */
export function sortDepartments(rows: readonly ActivityAnalysisDepartmentRow[], sort: SortState | null): ActivityAnalysisDepartmentRow[] {
  if (!sort) return [...rows];
  const other = rows.filter((r) => r.other);
  const rest = rows.filter((r) => !r.other);
  const direction = sort.descending ? -1 : 1;

  const sorted = [...rest].sort((a, b) => {
    if (sort.column.kind === 'name') {
      if (a.name == null || b.name == null) return a.name == null ? (b.name == null ? 0 : 1) : -1;
      return direction * compareStrings(a.name, b.name);
    }
    const difference = valueOf(a, sort.column) - valueOf(b, sort.column);
    if (difference !== 0) return direction * difference;
    return compareStrings(a.name ?? '\uffff', b.name ?? '\uffff');
  });

  return [...sorted, ...other];
}

function valueMap(values: readonly ActivityAnalysisMetricValue[]): Map<string, ActivityAnalysisMetricValue> {
  return new Map(values.map((v) => [v.metric, v]));
}

interface MatrixColumns {
  keys: string[];
  metrics: ReadonlyMap<string, ActivityAnalysisMetric>;
}

/** A department's people, loaded when it is expanded. See PII only - the caller never renders it otherwise. */
function DepartmentPeopleRows({
  query,
  row,
  columns,
  cache,
}: {
  query: ActivityAnalysisQuery;
  row: ActivityAnalysisDepartmentRow;
  columns: MatrixColumns;
  cache: Map<string, ActivityAnalysisPeople>;
}) {
  const t = useT();
  const styles = useActivityTableStyles();
  const sortMetric = query.metrics[0];
  const peopleQuery = useMemo(
    () => ({
      ...query,
      department: row.name ?? undefined,
      noDepartment: row.name == null,
      sort: sortMetric,
      top: PEOPLE_PER_DEPARTMENT,
    }),
    [query, row.name, sortMetric],
  );
  const key = JSON.stringify(peopleQuery);
  const [state, setState] = useState<{ key: string; data?: ActivityAnalysisPeople; error?: string } | null>(() => {
    const cached = cache.get(key);
    return cached ? { key, data: cached } : null;
  });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const cached = cache.get(key);
    if (cached) {
      setState({ key, data: cached });
      return;
    }

    const controller = new AbortController();
    setState({ key });
    fetchActivityAnalysisPeople(peopleQuery, controller.signal)
      .then((data) => {
        cache.set(key, data);
        if (!controller.signal.aborted) setState({ key, data });
      })
      .catch((e: unknown) => {
        if (controller.signal.aborted) return;
        setState({ key, error: e instanceof Error ? e.message : String(e) });
      });
    return () => controller.abort();
  }, [key, attempt]);

  const span = 2 + columns.keys.length * 2;
  const current = state?.key === key ? state : null;

  const note = (content: ReactNode) => (
    <tr className={styles.noteRow}>
      <td className={styles.td} colSpan={span}>
        <span className={styles.noteContent}>{content}</span>
      </td>
    </tr>
  );

  if (!current || (!current.data && !current.error)) {
    return note(
      <>
        <Spinner size="extra-tiny" />
        {t('activityAnalysis.matrix.loadingPeople')}
      </>,
    );
  }

  if (current.error) {
    return note(
      <>
        {current.error}
        <Button size="small" onClick={() => setAttempt((a) => a + 1)}>
          {t('activityAnalysis.page.retry')}
        </Button>
      </>,
    );
  }

  const data = current.data!;
  if (data.people.length === 0) return note(t('activityAnalysis.matrix.noPeople'));

  return (
    <>
      {data.people.map((person) => {
        const values = new Map(person.values.map((v) => [v.metric, v.sum]));
        return (
          <tr key={person.userPrincipalName} className={mergeClasses(styles.row, styles.personRow)}>
            <th scope="row" className={mergeClasses(styles.td, styles.first, styles.personName)}>
              <span className={styles.nameCell}>
                <span className={styles.name} title={person.userPrincipalName}>
                  {person.userPrincipalName}
                </span>
              </span>
            </th>
            <td className={styles.td} />
            {columns.keys.map((metricKey) => {
              const value = values.get(metricKey);
              return (
                <Fragment key={metricKey}>
                  <td className={mergeClasses(styles.td, styles.groupStart)}>
                    {value == null ? '' : formatMetricValue(columns.metrics.get(metricKey), value)}
                  </td>
                  <td className={styles.td} />
                </Fragment>
              );
            })}
          </tr>
        );
      })}
      {data.totalPeople > data.people.length &&
        note(
          t('activityAnalysis.matrix.showingPeople', {
            shown: formatNumber(data.people.length),
            total: formatNumber(data.totalPeople),
            metric: metricLabel(t, sortMetric, columns.metrics.get(sortMetric)?.label),
          }),
        )}
    </>
  );
}

function SortHeader({
  label,
  column,
  sort,
  onSort,
  className,
}: {
  label: string;
  column: SortColumn;
  sort: SortState | null;
  onSort: (column: SortColumn) => void;
  className?: string;
}) {
  const styles = useActivityTableStyles();
  const active = sort !== null && sameColumn(sort.column, column);
  const icon = active ? (sort!.descending ? <ArrowSortDown16Regular /> : <ArrowSortUp16Regular />) : undefined;
  return (
    <Button
      appearance="transparent"
      size="small"
      className={mergeClasses(styles.sortButton, className)}
      icon={icon}
      iconPosition="after"
      onClick={() => onSort(column)}
    >
      {label}
    </Button>
  );
}

function ariaSort(sort: SortState | null, column: SortColumn): 'ascending' | 'descending' | undefined {
  if (!sort || !sameColumn(sort.column, column)) return undefined;
  return sort.descending ? 'descending' : 'ascending';
}

function nameLabel(t: TFunction, row: ActivityAnalysisDepartmentRow, otherGroups: number): string {
  return row.other ? t('activityAnalysis.matrix.other') : groupName(t, row, 'department', otherGroups);
}

/**
 * The results matrix: one row per department, a Sum and a Unique column per selected metric, and a
 * Total row pinned to the foot - the Power BI matrix. A reader with See PII can expand a department to
 * its people, loaded on demand; nobody else is offered the expander, so the people endpoint is never
 * called for a reader the server would refuse.
 *
 * The first column stays put while a wide selection scrolls sideways under it, and the header stays
 * put while a long department list scrolls under it.
 */
export default function ResultsMatrix({
  report,
  query,
  metrics,
}: {
  report: ActivityAnalysisReport;
  /** The query `report` answered - the people under a department are asked for with the same one. */
  query: ActivityAnalysisQuery;
  metrics: ReadonlyMap<string, ActivityAnalysisMetric>;
}) {
  const styles = useStyles();
  const table = useActivityTableStyles();
  const t = useT();
  const { seePii, roles } = usePortalAccess();
  const [sort, setSort] = useState<SortState | null>(null);
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set());
  // People already loaded, per query and department, so collapsing and re-expanding is instant.
  const cache = useRef(new Map<string, ActivityAnalysisPeople>()).current;

  const columns: MatrixColumns = { keys: report.metrics, metrics };
  const rows = useMemo(() => sortDepartments(report.departments, sort), [report.departments, sort]);
  const total = valueMap(report.total.values);

  // How many rows are drawn, for the report it was raised for: a new report starts from one page again.
  const [page, setPage] = useState({ departments: report.departments, count: DEPARTMENTS_PER_PAGE });
  const drawn = page.departments === report.departments ? page.count : DEPARTMENTS_PER_PAGE;
  const visibleRows = rows.length > drawn ? rows.slice(0, drawn) : rows;
  const hiddenRows = rows.length - visibleRows.length;

  const onSort = (column: SortColumn) =>
    setSort((current) => {
      if (current && sameColumn(current.column, column)) return { column, descending: !current.descending };
      // Figures read largest first; names read A to Z.
      return { column, descending: column.kind !== 'name' };
    });

  const toggle = (key: string) =>
    setExpanded((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  const title = t('activityAnalysis.matrix.title');
  const span = 2 + report.metrics.length * 2;

  return (
    <Card className={styles.card}>
      <div className={styles.head}>
        <Text as="h2" weight="semibold" size={500} style={{ margin: 0 }}>
          {title}
        </Text>
        <InfoTip
          title={title}
          content={{ what: t('activityAnalysis.matrix.info.what'), how: t('activityAnalysis.matrix.info.how') }}
        />
      </div>

      <div className={table.scroll} role="region" aria-label={title} tabIndex={0}>
        <table className={table.table}>
          <caption className={table.caption}>{t('activityAnalysis.matrix.caption')}</caption>
          <thead>
            <tr>
              <th
                rowSpan={2}
                scope="col"
                className={mergeClasses(table.th, table.corner)}
                aria-sort={ariaSort(sort, { kind: 'name' })}
              >
                <span className={table.nameCell}>
                  <span className={table.expanderSpacer} />
                  <SortHeader label={t('activityAnalysis.matrix.department')} column={{ kind: 'name' }} sort={sort} onSort={onSort} />
                </span>
              </th>
              <th rowSpan={2} scope="col" className={table.th} aria-sort={ariaSort(sort, { kind: 'people' })}>
                <SortHeader label={t('activityAnalysis.matrix.people')} column={{ kind: 'people' }} sort={sort} onSort={onSort} />
              </th>
              {report.metrics.map((key) => {
                const label = metricLabelWithUnit(t, key, metrics.get(key));
                return (
                  <th
                    key={key}
                    colSpan={2}
                    scope="colgroup"
                    className={mergeClasses(table.th, table.thFirstRow, table.thMetric, table.groupStart)}
                  >
                    <span className={table.metricName} title={label}>
                      {label}
                    </span>
                  </th>
                );
              })}
            </tr>
            <tr>
              {report.metrics.map((key) => {
                const label = metricLabel(t, key, metrics.get(key)?.label);
                const sumColumn: SortColumn = { kind: 'sum', metric: key };
                const uniqueColumn: SortColumn = { kind: 'unique', metric: key };
                return (
                  <Fragment key={key}>
                    <th
                      scope="col"
                      className={mergeClasses(table.th, table.thSecondRow, table.groupStart)}
                      title={t('activityAnalysis.matrix.sumTitle', { metric: label })}
                      aria-sort={ariaSort(sort, sumColumn)}
                    >
                      <SortHeader label={t('activityAnalysis.matrix.sum')} column={sumColumn} sort={sort} onSort={onSort} />
                    </th>
                    <th
                      scope="col"
                      className={mergeClasses(table.th, table.thSecondRow)}
                      title={t('activityAnalysis.matrix.uniqueTitle', { metric: label })}
                      aria-sort={ariaSort(sort, uniqueColumn)}
                    >
                      <SortHeader label={t('activityAnalysis.matrix.unique')} column={uniqueColumn} sort={sort} onSort={onSort} />
                    </th>
                  </Fragment>
                );
              })}
            </tr>
          </thead>

          <tbody>
            {rows.length === 0 && (
              <tr className={table.noteRow}>
                <td className={table.td} colSpan={span}>
                  <span className={table.noteContent}>{t('activityAnalysis.matrix.empty')}</span>
                </td>
              </tr>
            )}
            {visibleRows.map((row) => {
              const key = departmentKey(row);
              const label = nameLabel(t, row, report.otherDepartments);
              const expandable = seePii && !row.other;
              const open = expandable && expanded.has(key);
              const values = valueMap(row.values);
              return (
                <Fragment key={key}>
                  <tr
                    className={mergeClasses(table.row, row.other ? table.otherRow : undefined)}
                    aria-expanded={expandable ? open : undefined}
                  >
                    <th scope="row" className={mergeClasses(table.td, table.first)}>
                      <span className={table.nameCell}>
                        {expandable ? (
                          <Button
                            appearance="transparent"
                            size="small"
                            className={table.expander}
                            icon={open ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
                            aria-expanded={open}
                            aria-label={
                              open
                                ? t('activityAnalysis.matrix.collapse', { department: label })
                                : t('activityAnalysis.matrix.expand', { department: label })
                            }
                            onClick={() => toggle(key)}
                            data-print="hide"
                          />
                        ) : (
                          <span className={table.expanderSpacer} />
                        )}
                        <span className={table.name} title={label}>
                          {label}
                        </span>
                        {row.other && report.otherDepartments > 0 && (
                          <span className={table.nameDetail}>
                            {t(plural(report.otherDepartments, 'activityAnalysis.matrix.otherCount.one', 'activityAnalysis.matrix.otherCount.other'), {
                              count: formatNumber(report.otherDepartments),
                            })}
                          </span>
                        )}
                      </span>
                    </th>
                    <td className={table.td}>{formatNumber(row.people)}</td>
                    {report.metrics.map((metricKey) => {
                      const value = values.get(metricKey);
                      return (
                        <Fragment key={metricKey}>
                          <td className={mergeClasses(table.td, table.groupStart)}>
                            {value ? formatMetricValue(metrics.get(metricKey), value.sum) : '\u2013'}
                          </td>
                          <td className={table.td}>{value ? formatNumber(value.unique) : '\u2013'}</td>
                        </Fragment>
                      );
                    })}
                  </tr>
                  {open && <DepartmentPeopleRows query={query} row={row} columns={columns} cache={cache} />}
                </Fragment>
              );
            })}
            {hiddenRows > 0 && (
              <tr className={table.noteRow}>
                <td className={table.td} colSpan={span}>
                  <span className={table.noteContent}>
                    <span>
                      {t('activityAnalysis.matrix.showingRows', {
                        shown: formatNumber(visibleRows.length),
                        total: formatNumber(rows.length),
                      })}
                    </span>
                    <Button
                      size="small"
                      data-print="hide"
                      onClick={() => setPage({ departments: report.departments, count: drawn + DEPARTMENTS_PER_PAGE })}
                    >
                      {t('activityAnalysis.matrix.showMoreRows', {
                        count: formatNumber(Math.min(DEPARTMENTS_PER_PAGE, hiddenRows)),
                      })}
                    </Button>
                  </span>
                </td>
              </tr>
            )}
          </tbody>

          <tfoot>
            <tr className={table.total}>
              <th scope="row" className={mergeClasses(table.td, table.first)}>
                <span className={table.nameCell}>
                  <span className={table.expanderSpacer} />
                  {t('activityAnalysis.matrix.total')}
                </span>
              </th>
              <td className={table.td}>{formatNumber(report.total.people)}</td>
              {report.metrics.map((metricKey) => {
                const value = total.get(metricKey);
                return (
                  <Fragment key={metricKey}>
                    <td className={mergeClasses(table.td, table.groupStart)}>
                      {value ? formatMetricValue(metrics.get(metricKey), value.sum) : '\u2013'}
                    </td>
                    <td className={table.td}>{value ? formatNumber(value.unique) : '\u2013'}</td>
                  </Fragment>
                );
              })}
            </tr>
          </tfoot>
        </table>
      </div>

      {!seePii && (
        <Text size={200} className={styles.note}>
          {t('activityAnalysis.matrix.piiNote', { role: roles.seePii })}
        </Text>
      )}
    </Card>
  );
}
